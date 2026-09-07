using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using GdiColor = System.Drawing.Color;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using GdiPointF = System.Drawing.PointF;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private readonly record struct Reference3DPlaybackBackgroundKey(
        int Width,
        int Height,
        int BackColorArgb,
        float WorldGridOpacity,
        WorldGridType WorldGridType,
        float PlanarWorldGridOpacity,
        float ReferenceWorldGridOpacity,
        SceneDimension ReferenceDimension,
        CameraProjection ReferenceProjection,
        CameraProjection EffectiveReferenceProjection,
        float ReferenceProjectionBlend,
        float EffectiveReferenceYaw,
        float EffectiveReferencePitch,
        float ReferenceDistance,
        float ReferenceZoomScale,
        float ReferenceTargetX,
        float ReferenceTargetY,
        float ReferenceTargetZ,
        string FontName,
        float FontSize,
        FontStyle FontStyle,
        GraphicsUnit FontUnit);

    private const int Reference3DCpuRasterMinimumCommands = 96;
    private const int Reference3DCpuRasterTileSize = 128;
    private const int Reference3DCpuRasterMaximumPixels = 16_777_216;
    private const int Reference3DCpuRasterMaximumBinEntries = 8_388_608;
    private const int Reference3DCpuRasterDynamicResolutionCommandThreshold = 192;
    private const float Reference3DCpuRasterDynamicResolutionScale = 0.7f;
    private const int Reference3DCpuRasterGradientLookupSize = 2048;

    private int[]? _reference3DCpuRasterPixels;
    private int[]? _reference3DCpuRasterTileCommandCounts;
    private int[]? _reference3DCpuRasterTileCommandOffsets;
    private int[]? _reference3DCpuRasterCommandIndices;
    private int[]? _reference3DCpuRasterCommandTileBounds;
    private ID2D1Bitmap? _reference3DCpuRasterBitmap;
    private int _reference3DCpuRasterBitmapWidth;
    private int _reference3DCpuRasterBitmapHeight;
    private long _reference3DCpuRasterBitmapTargetGeneration;
    private Bitmap? _reference3DFastPlaybackBitmap;
    private Graphics? _reference3DFastPlaybackBitmapGraphics;
    private Bitmap? _reference3DPlaybackPresentedBitmap;
    private Graphics? _reference3DPlaybackPresentedBitmapGraphics;
    private Reference3DPlaybackGdiRasterSurface? _reference3DPlaybackGdiRasterSurface;
    private int _reference3DFastPlaybackBitmapWidth;
    private int _reference3DFastPlaybackBitmapHeight;
    private Bitmap? _reference3DPlaybackBackgroundBitmap;
    private Graphics? _reference3DPlaybackBackgroundBitmapGraphics;
    private int _reference3DPlaybackBackgroundBitmapWidth;
    private int _reference3DPlaybackBackgroundBitmapHeight;
    private int[]? _reference3DPlaybackBackgroundPixels;
    private Reference3DPlaybackBackgroundKey _reference3DPlaybackBackgroundKey;
    private bool _reference3DPlaybackBackgroundReady;

    private bool _reference3DCpuRasterUsedThisFrame;

    public int LastReference3DCpuRasterWorkers { get; private set; }

    public int LastReference3DCpuRasterTiles { get; private set; }

    public int LastReference3DCpuRasterCommands { get; private set; }

    public double LastReference3DCpuRasterMilliseconds { get; private set; }

    public double LastReference3DCpuRasterUploadMilliseconds { get; private set; }

    public int LastReference3DCpuRasterBitmapBuilds { get; private set; }

    public int LastReference3DCpuRasterBitmapReuses { get; private set; }

    public int LastReference3DCpuRasterPreparationWorkers { get; private set; }

    public double LastReference3DCpuRasterPreparationMilliseconds { get; private set; }

    public double LastReference3DPlaybackBackgroundMilliseconds { get; private set; }

    public double LastReference3DPlaybackBackgroundReadbackMilliseconds { get; private set; }

    public double LastReference3DPlaybackBitmapWriteMilliseconds { get; private set; }

    public double LastReference3DPlaybackBlitMilliseconds { get; private set; }

    public float LastReference3DCpuRasterScale { get; private set; } = 1f;

    public string LastReference3DCpuRasterFallbackReason { get; private set; } = "not_attempted";

    public string LastReference3DPlaybackGdiStatus { get; private set; } = "not_attempted";

    private static string _lastReference3DPlaybackGdiRasterFailure = "none";

    internal string LastReference3DPlaybackGdiRasterFailure =>
        Volatile.Read(ref _lastReference3DPlaybackGdiRasterFailure);

    private sealed class Reference3DCpuRasterCommand
    {
        public Reference3DCpuRasterCommand(
            bool fill,
            GdiPointF[][] contours,
            Reference3DCpuRasterSegment[] segments,
            float minX,
            float minY,
            float maxX,
            float maxY,
            int solidArgb,
            float strokeWidth,
            GradientKind gradientKind,
            Reference3DCpuRasterGradientStop[] gradientStops,
            int[] gradientLookup,
            Vector2 gradientStartScreen,
            Vector2 gradientEndScreen,
            Vector2 gradientStartSource,
            float gradientRadiusSquared,
            Matrix3x2 screenToSource,
            float rasterScale,
            int pointCount,
            int rasterHeight,
            bool gradientStopsOpaque,
            bool singleClosedContour,
            int fillScanlineStartY,
            int[] fillScanlineOffsets,
            Reference3DCpuRasterSpan[] fillScanlineSpans,
            bool fillScanlinesReady)
        {
            Fill = fill;
            Contours = contours;
            Segments = segments;
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
            SolidArgb = solidArgb;
            PremultipliedSolidArgb = PremultiplyReference3DCpuArgb(solidArgb);
            StrokeWidth = strokeWidth;
            GradientKind = gradientKind;
            GradientStops = gradientStops;
            GradientLookup = gradientLookup;
            GradientStartScreen = gradientStartScreen;
            GradientEndScreen = gradientEndScreen;
            GradientStartSource = gradientStartSource;
            GradientRadiusSquared = gradientRadiusSquared;
            ScreenToSource = screenToSource;
            RasterScale = rasterScale;
            HasGradient = gradientKind != GradientKind.Solid && gradientStops.Length > 0;
            Opaque = (gradientStops.Length == 0 || gradientStopsOpaque)
                && (solidArgb >> 24 & 0xff) == 255;
            var gradientAxisX = gradientEndScreen.X - gradientStartScreen.X;
            var gradientAxisY = gradientEndScreen.Y - gradientStartScreen.Y;
            var gradientLengthSquared = gradientAxisX * gradientAxisX
                + gradientAxisY * gradientAxisY;
            GradientAxisX = gradientAxisX;
            GradientAxisY = gradientAxisY;
            GradientInverseLengthSquared = float.IsFinite(gradientLengthSquared)
                && gradientLengthSquared > 0.000001f
                    ? 1f / gradientLengthSquared
                    : 0f;
            PointCount = pointCount;
            RasterHeight = rasterHeight;
            SingleClosedContour = singleClosedContour;
            FillScanlineStartY = fillScanlineStartY;
            FillScanlineOffsets = fillScanlineOffsets;
            FillScanlineSpans = fillScanlineSpans;
            _fillScanlinesReady = fillScanlinesReady ? 1 : 0;
        }

        public bool Fill { get; }

        public GdiPointF[][] Contours { get; }

        public Reference3DCpuRasterSegment[] Segments { get; }

        public float MinX { get; }

        public float MinY { get; }

        public float MaxX { get; }

        public float MaxY { get; }

        public int SolidArgb { get; }

        public int PremultipliedSolidArgb { get; }

        public float StrokeWidth { get; }

        public GradientKind GradientKind { get; }

        public Reference3DCpuRasterGradientStop[] GradientStops { get; }

        public int[] GradientLookup { get; private set; }

        private readonly object _gradientLookupGate = new();

        public Vector2 GradientStartScreen { get; }

        public Vector2 GradientEndScreen { get; }

        public Vector2 GradientStartSource { get; }

        public float GradientRadiusSquared { get; }

        public Matrix3x2 ScreenToSource { get; }

        public float RasterScale { get; }

        public bool HasGradient { get; }

        public bool Opaque { get; }

        public float GradientAxisX { get; }

        public float GradientAxisY { get; }

        public float GradientInverseLengthSquared { get; }

        public int PointCount { get; }

        public bool SingleClosedContour { get; }

        private readonly int RasterHeight;

        private int _fillScanlinesReady;

        private readonly object _fillScanlineGate = new();

        public int FillScanlineStartY { get; private set; }

        public int[] FillScanlineOffsets { get; private set; }

        public Reference3DCpuRasterSpan[] FillScanlineSpans { get; private set; }

        public void EnsureGradientLookup()
        {
            if (!HasGradient || GradientLookup.Length > 0) return;

            lock (_gradientLookupGate)
            {
                if (!HasGradient || GradientLookup.Length > 0) return;
                var lookup = new int[Reference3DCpuRasterGradientLookupSize];
                for (var index = 0; index < lookup.Length; index++)
                {
                    lookup[index] = SampleGradientArgb(
                        GradientStops,
                        index / (float)(lookup.Length - 1));
                }
                GradientLookup = lookup;
            }
        }

        public void EnsureFillScanlines()
        {
            if (!Fill || Volatile.Read(ref _fillScanlinesReady) != 0) return;

            lock (_fillScanlineGate)
            {
                if (Volatile.Read(ref _fillScanlinesReady) != 0) return;
                BuildReference3DCpuFillScanlines(
                    Segments,
                    MinY,
                    MaxY,
                    RasterHeight,
                    out var startY,
                    out var offsets,
                    out var spans);
                FillScanlineStartY = startY;
                FillScanlineOffsets = offsets;
                FillScanlineSpans = spans;
                Volatile.Write(ref _fillScanlinesReady, 1);
            }
        }

        public int SampleArgb(float x, float y)
        {
            if (!HasGradient)
            {
                return SolidArgb;
            }

            float position;
            if (GradientKind == GradientKind.Linear)
            {
                if (GradientInverseLengthSquared <= 0f)
                {
                    return GradientStops[^1].Argb;
                }

                position = ((x - GradientStartScreen.X) * GradientAxisX
                    + (y - GradientStartScreen.Y) * GradientAxisY)
                    * GradientInverseLengthSquared;
            }
            else
            {
                var inverseRasterScale = RasterScale > 0.000001f
                    ? 1f / RasterScale
                    : 1f;
                var source = Vector2.Transform(
                    new Vector2(x * inverseRasterScale, y * inverseRasterScale),
                    ScreenToSource);
                var delta = source - GradientStartSource;
                var distanceSquared = delta.LengthSquared();
                position = !float.IsFinite(distanceSquared)
                    || GradientRadiusSquared <= 0.000001f
                    ? 1f
                    : MathF.Sqrt(Math.Max(0f, distanceSquared / GradientRadiusSquared));
            }

            if (GradientLookup.Length > 0)
            {
                var lookupIndex = Math.Clamp(
                    (int)(Math.Clamp(position, 0f, 1f)
                        * (GradientLookup.Length - 1)
                        + 0.5f),
                    0,
                    GradientLookup.Length - 1);
                return GradientLookup[lookupIndex];
            }

            return SampleGradientArgb(GradientStops, position);
        }
    }

    private readonly record struct Reference3DCpuRasterSegment
    {
        public Reference3DCpuRasterSegment(GdiPointF a, GdiPointF b)
        {
            A = a;
            B = b;
            Dx = b.X - a.X;
            Dy = b.Y - a.Y;
            LengthSquared = Dx * Dx + Dy * Dy;
            InverseLengthSquared = float.IsFinite(LengthSquared)
                && LengthSquared > 0.000001f
                    ? 1f / LengthSquared
                    : 0f;
            InverseDy = float.IsFinite(Dy) && Math.Abs(Dy) > 0.000001f
                ? 1f / Dy
                : 0f;
            MinX = Math.Min(a.X, b.X);
            MaxX = Math.Max(a.X, b.X);
            MinY = Math.Min(a.Y, b.Y);
            MaxY = Math.Max(a.Y, b.Y);
        }

        public GdiPointF A { get; }

        public GdiPointF B { get; }

        public float Dx { get; }

        public float Dy { get; }

        public float LengthSquared { get; }

        public float InverseLengthSquared { get; }

        public float InverseDy { get; }

        public float MinX { get; }

        public float MaxX { get; }

        public float MinY { get; }

        public float MaxY { get; }
    }

    private readonly record struct Reference3DCpuRasterSpan(float Start, float End);

    private readonly record struct Reference3DCpuRasterGradientStop(
        float Position,
        byte A,
        byte R,
        byte G,
        byte B)
    {
        public int Argb => A << 24 | R << 16 | G << 8 | B;
    }

    private readonly record struct Reference3DCpuRasterGeometry(
        GdiPointF[][] Contours,
        Reference3DCpuRasterSegment[] Segments,
        float MinX,
        float MinY,
        float MaxX,
        float MaxY,
        int PointCount,
        bool SingleClosedContour);

    private sealed class Reference3DCpuRasterTileBins(
        Reference3DCpuRasterCommand[] commands,
        int width,
        int height,
        int tilesX,
        int tilesY,
        int[] commandOffsets,
        int[] commandIndices)
    {
        public Reference3DCpuRasterCommand[] Commands { get; } = commands;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int TilesX { get; } = tilesX;
        public int TilesY { get; } = tilesY;
        public int[] CommandOffsets { get; } = commandOffsets;
        public int[] CommandIndices { get; } = commandIndices;

        public bool Matches(
            int width,
            int height,
            int tilesX,
            int tilesY,
            Reference3DCpuRasterCommand[] commands) =>
            Width == width
            && Height == height
            && TilesX == tilesX
            && TilesY == tilesY
            && ReferenceEquals(Commands, commands);
    }

    private readonly record struct Reference3DCpuRasterGradientProfile(
        Reference3DCpuRasterGradientStop[] Stops,
        int[] Lookup,
        bool Opaque);

    private readonly record struct Reference3DCpuRasterLinearGradientProjection(
        bool Succeeded,
        bool UseSolidFallback,
        Vector2 Start,
        Vector2 End);

    private bool TryDrawReference3DCpuRaster(
        StageControl stage,
        IReadOnlyList<Reference3DRenderItem> renderItems,
        out int drawnObjects)
    {
        drawnObjects = 0;
        if (stage.Reference3DPlaybackActive
            && Environment.GetEnvironmentVariable("VECTOR_DISABLE_PLAYBACK_CPU_RASTER") == "1")
        {
            LastReference3DCpuRasterFallbackReason = "disabled_by_environment";
            return false;
        }
        if (!TryRasterizeReference3DCpuRaster(
                stage,
                renderItems,
                _targetSize.Width,
                _targetSize.Height,
                out var pixels,
                out var width,
                out var height,
                out var rasterScale,
                out drawnObjects))
        {
            return false;
        }

        ID2D1Bitmap? bitmap = null;
        var uploadStarted = Stopwatch.GetTimestamp();
        try
        {
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var properties = new BitmapProperties(
                    new D2DPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                    96,
                    96);
                var pitch = checked((uint)(width * sizeof(int)));
                var cachedBitmap = _reference3DCpuRasterBitmap;
                var canReuse = cachedBitmap is not null
                    && _reference3DCpuRasterBitmapWidth == width
                    && _reference3DCpuRasterBitmapHeight == height
                    && _reference3DCpuRasterBitmapTargetGeneration == _targetGeneration;
                if (canReuse)
                {
                    var copyResult = cachedBitmap!.CopyFromMemory(
                        handle.AddrOfPinnedObject(),
                        pitch);
                    if (copyResult.Failure)
                    {
                        throw new InvalidOperationException(
                            "Direct2D CPU raster bitmap upload failed.");
                    }

                    bitmap = cachedBitmap;
                    LastReference3DCpuRasterBitmapReuses++;
                }
                else
                {
                    bitmap = _target!.CreateBitmap(
                        new SizeI(width, height),
                        handle.AddrOfPinnedObject(),
                        pitch,
                        properties);
                    var previousBitmap = _reference3DCpuRasterBitmap;
                    _reference3DCpuRasterBitmap = bitmap;
                    _reference3DCpuRasterBitmapWidth = width;
                    _reference3DCpuRasterBitmapHeight = height;
                    _reference3DCpuRasterBitmapTargetGeneration = _targetGeneration;
                    previousBitmap?.Dispose();
                    LastReference3DCpuRasterBitmapBuilds++;
                }
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            LastReference3DCpuRasterFallbackReason = "bitmap_upload_failed";
            ClearReference3DCpuRasterBitmap();
            return false;
        }

        var uploadMilliseconds = Stopwatch.GetElapsedTime(uploadStarted).TotalMilliseconds;
        try
        {
            var previousTransform = _target!.Transform;
            try
            {
                _target.Transform = Matrix3x2.Identity;
                _target.DrawBitmap(
                    bitmap,
                    Rect(0, 0, _targetSize.Width, _targetSize.Height),
                    1f,
                    rasterScale < 0.999999f
                        ? BitmapInterpolationMode.Linear
                        : BitmapInterpolationMode.NearestNeighbor,
                    Rect(0, 0, width, height));
            }
            finally
            {
                _target.Transform = previousTransform;
            }
        }
        catch
        {
            ClearReference3DCpuRasterBitmap();
            throw;
        }
        LastReference3DCpuRasterUploadMilliseconds += uploadMilliseconds;
        return true;
    }

    internal bool TryRenderReference3DPlaybackGdi(
        StageControl stage,
        Graphics graphics,
        out RenderStats stats)
    {
        stats = default;
        if (Environment.GetEnvironmentVariable("VECTOR_DISABLE_PLAYBACK_GDI_PRESENT") == "1")
        {
            LastReference3DPlaybackGdiStatus = "disabled_by_environment";
            return false;
        }
        if (!stage.Reference3DPlaybackActive)
        {
            LastReference3DPlaybackGdiStatus = "inactive";
            return false;
        }
        if (!stage.RendersReferenceProjection)
        {
            LastReference3DPlaybackGdiStatus = "not_reference_projection";
            return false;
        }
        if (stage.UnderlayScene is not null)
        {
            LastReference3DPlaybackGdiStatus = "underlay";
            return false;
        }
        if (stage.OnionSkinScene is not null)
        {
            LastReference3DPlaybackGdiStatus = "onion_skin";
            return false;
        }
        if (stage.DragPreviewScene is not null)
        {
            LastReference3DPlaybackGdiStatus = "drag_preview";
            return false;
        }
        if (stage.SceneCompositionMaskClips.Count > 0)
        {
            LastReference3DPlaybackGdiStatus = "composition_mask";
            return false;
        }
        if (stage.Scene.HasNonNormalLayerBlendModes)
        {
            LastReference3DPlaybackGdiStatus = "non_normal_blend";
            return false;
        }
        if (stage.SceneHasDistortionsForRendering(stage.Scene))
        {
            LastReference3DPlaybackGdiStatus = "distortion";
            return false;
        }
        if (stage.Width <= 0 || stage.Height <= 0)
        {
            LastReference3DPlaybackGdiStatus = "invalid_size";
            return false;
        }

        LastReference3DPlaybackGdiStatus = "eligible";
        ResetReference3DCpuRasterMetrics();
        var commandStarted = Stopwatch.GetTimestamp();
        try
        {
            EnsureReference3DPlaybackBackground(stage);
            EnsureReference3DFastPlaybackBitmap(stage.Width, stage.Height);
            if (!TryPresentReference3DPlaybackRaster(stage, graphics, out stats))
            {
                LastReference3DPlaybackGdiStatus = "present_failed";
                return false;
            }
            LastReference3DPlaybackGdiStatus = "presented";
            LastCommandMilliseconds = Stopwatch.GetElapsedTime(commandStarted).TotalMilliseconds;
            LastPresentMilliseconds = 0;
            return true;
        }
        catch (Exception exception)
        {
            LastReference3DCpuRasterFallbackReason = "gdi_playback_exception";
            AppLog.Warn($"Fast 3D playback raster skipped: {exception.Message}");
            return false;
        }
    }

    private void EnsureReference3DPlaybackBackground(StageControl stage)
    {
        var key = CreateReference3DPlaybackBackgroundKey(stage);
        var pixelCount = checked(stage.Width * stage.Height);
        var cachedBackgroundPixels = _reference3DPlaybackBackgroundPixels;
        if (_reference3DPlaybackBackgroundReady
            && _reference3DPlaybackBackgroundKey == key
            && cachedBackgroundPixels is not null
            && cachedBackgroundPixels.Length >= pixelCount)
        {
            stage.BeginScenePassOrder();
            stage.RecordEditableScenePass();
            return;
        }

        var bitmap = EnsureReference3DPlaybackBackgroundBitmap(stage.Width, stage.Height);
        var backgroundStarted = Stopwatch.GetTimestamp();
        stage.DrawGdiReference3DPlaybackBackground(
            _reference3DPlaybackBackgroundBitmapGraphics!);
        LastReference3DPlaybackBackgroundMilliseconds =
            Stopwatch.GetElapsedTime(backgroundStarted).TotalMilliseconds;

        // Completed playback workers keep the previously published array.
        // Replace it on a background change instead of mutating an array that
        // a worker may still be reading.
        var backgroundPixels = new int[pixelCount];

        var readbackStarted = Stopwatch.GetTimestamp();
        CopyReference3DPlaybackBitmapToPixels(
            bitmap,
            backgroundPixels,
            stage.Width,
            stage.Height);
        LastReference3DPlaybackBackgroundReadbackMilliseconds =
            Stopwatch.GetElapsedTime(readbackStarted).TotalMilliseconds;
        _reference3DPlaybackBackgroundPixels = backgroundPixels;
        _reference3DPlaybackBackgroundKey = key;
        _reference3DPlaybackBackgroundReady = true;
    }

    private static Reference3DPlaybackBackgroundKey CreateReference3DPlaybackBackgroundKey(
        StageControl stage)
    {
        var font = stage.Font;
        return new Reference3DPlaybackBackgroundKey(
            stage.Width,
            stage.Height,
            stage.BackColor.ToArgb(),
            stage.WorldGridOpacity,
            stage.WorldGridType,
            stage.PlanarWorldGridOpacity,
            stage.ReferenceWorldGridOpacity,
            stage.ReferenceDimension,
            stage.ReferenceProjection,
            stage.EffectiveReferenceProjection,
            stage.ReferenceProjectionBlend,
            stage.EffectiveReferenceYaw,
            stage.EffectiveReferencePitch,
            stage.ReferenceDistance,
            stage.ReferenceZoomScale,
            stage.ReferenceTargetX,
            stage.ReferenceTargetY,
            stage.ReferenceTargetZ,
            font.Name,
            font.Size,
            font.Style,
            font.Unit);
    }

    private Bitmap EnsureReference3DPlaybackBackgroundBitmap(int width, int height)
    {
        if (_reference3DPlaybackBackgroundBitmap is not null
            && _reference3DPlaybackBackgroundBitmapWidth == width
            && _reference3DPlaybackBackgroundBitmapHeight == height)
        {
            return _reference3DPlaybackBackgroundBitmap;
        }

        _reference3DPlaybackBackgroundBitmapGraphics?.Dispose();
        _reference3DPlaybackBackgroundBitmapGraphics = null;
        _reference3DPlaybackBackgroundBitmap?.Dispose();
        _reference3DPlaybackBackgroundBitmap = new Bitmap(
            width,
            height,
            GdiPixelFormat.Format32bppPArgb);
        _reference3DPlaybackBackgroundBitmapGraphics = Graphics.FromImage(
            _reference3DPlaybackBackgroundBitmap);
        _reference3DPlaybackBackgroundBitmapWidth = width;
        _reference3DPlaybackBackgroundBitmapHeight = height;
        _reference3DPlaybackBackgroundReady = false;
        _reference3DPlaybackBackgroundPixels = null;
        return _reference3DPlaybackBackgroundBitmap;
    }

    private bool TryRasterizeReference3DCpuRaster(
        StageControl stage,
        IReadOnlyList<Reference3DRenderItem> renderItems,
        int targetWidth,
        int targetHeight,
        out int[] pixels,
        out int width,
        out int height,
        out float rasterScale,
        out int drawnObjects,
        float? rasterScaleOverride = null,
        Action<int[], int, int>? initializePixels = null)
    {
        pixels = Array.Empty<int>();
        width = 0;
        height = 0;
        rasterScale = 1f;
        drawnObjects = 0;
        LastReference3DCpuRasterFallbackReason = "ineligible";
        if (!TryPrepareReference3DCpuRasterCommands(
                stage,
                renderItems,
                rasterScaleOverride,
                maxWorkers: null,
                buildFillScanlines: true,
                buildGradientLookup: true,
                out var commands,
                out rasterScale,
                out drawnObjects))
        {
            return false;
        }

        var resolvedRasterScale = rasterScale;

        var rasterWidth = Math.Max(1, (int)MathF.Round(targetWidth * resolvedRasterScale));
        var rasterHeight = Math.Max(1, (int)MathF.Round(targetHeight * resolvedRasterScale));
        var pixelCountLong = (long)rasterWidth * rasterHeight;
        if (rasterWidth <= 0
            || rasterHeight <= 0
            || pixelCountLong <= 0
            || pixelCountLong > Reference3DCpuRasterMaximumPixels)
        {
            LastReference3DCpuRasterFallbackReason = "raster_size_limit";
            return false;
        }

        var pixelCount = (int)pixelCountLong;
        var rasterPixels = EnsureReference3DCpuRasterPixels(pixelCount);
        try
        {
            if (initializePixels is null)
            {
                Array.Clear(rasterPixels, 0, pixelCount);
            }
            else
            {
                initializePixels(rasterPixels, rasterWidth, rasterHeight);
            }
        }
        catch
        {
            LastReference3DCpuRasterFallbackReason = "raster_background_failed";
            return false;
        }

        var tilesX = (rasterWidth + Reference3DCpuRasterTileSize - 1) / Reference3DCpuRasterTileSize;
        var tilesY = (rasterHeight + Reference3DCpuRasterTileSize - 1) / Reference3DCpuRasterTileSize;
        var tileCount = checked(tilesX * tilesY);
        if (!TryBuildReference3DCpuRasterCommandBins(
                commands,
                rasterWidth,
                rasterHeight,
                tilesX,
                tilesY,
                out var tileCommandOffsets,
                out var tileCommandIndices))
        {
            LastReference3DCpuRasterFallbackReason = "bin_build_failed";
            return false;
        }

        var workers = ParallelBatch.WorkerCount(tileCount, 1);
        var rasterStarted = Stopwatch.GetTimestamp();
        try
        {
            ParallelBatch.For(
                tileCount,
                1,
                (_, start, end) =>
                {
                    var coverage = ArrayPool<float>.Shared.Rent(Reference3DCpuRasterTileSize);
                    try
                    {
                        for (var tileIndex = start; tileIndex < end; tileIndex++)
                        {
                            var tileX = tileIndex % tilesX;
                            var tileY = tileIndex / tilesX;
                            var left = tileX * Reference3DCpuRasterTileSize;
                            var top = tileY * Reference3DCpuRasterTileSize;
                            var right = Math.Min(rasterWidth, left + Reference3DCpuRasterTileSize);
                            var bottom = Math.Min(rasterHeight, top + Reference3DCpuRasterTileSize);
                            RasterReference3DCpuTile(
                                rasterPixels,
                                rasterWidth,
                                commands,
                                tileCommandIndices,
                                tileCommandOffsets[tileIndex],
                                tileCommandOffsets[tileIndex + 1],
                                left,
                                top,
                                right,
                                bottom,
                                coverage);
                        }
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(coverage, clearArray: false);
                    }
                });
        }
        catch
        {
            LastReference3DCpuRasterFallbackReason = "raster_exception";
            return false;
        }

        LastReference3DCpuRasterWorkers = Math.Max(
            LastReference3DCpuRasterWorkers,
            workers);
        LastReference3DCpuRasterTiles += tileCount;
        LastReference3DCpuRasterCommands += commands.Length;
        LastReference3DCpuRasterMilliseconds +=
            Stopwatch.GetElapsedTime(rasterStarted).TotalMilliseconds;
        _reference3DCpuRasterUsedThisFrame = true;
        LastReference3DCpuRasterFallbackReason = "used";
        pixels = rasterPixels;
        width = rasterWidth;
        height = rasterHeight;
        return true;
    }

    private bool TryPrepareReference3DCpuRasterCommands(
        StageControl stage,
        IReadOnlyList<Reference3DRenderItem> renderItems,
        float? rasterScaleOverride,
        int? maxWorkers,
        bool buildFillScanlines,
        bool buildGradientLookup,
        out Reference3DCpuRasterCommand[] commands,
        out float rasterScale,
        out int drawnObjects)
    {
        commands = [];
        rasterScale = 1f;
        drawnObjects = 0;
        LastReference3DCpuRasterPreparationWorkers = 0;
        LastReference3DCpuRasterPreparationMilliseconds = 0;
        LastReference3DCpuRasterFallbackReason = "ineligible";
        if (!CanUseReference3DCpuRaster(stage, renderItems)) return false;

        var resolvedRasterScale = rasterScaleOverride
            ?? GetReference3DCpuRasterScale(stage, renderItems.Count);
        rasterScale = resolvedRasterScale;
        LastReference3DCpuRasterScale = resolvedRasterScale;
        var rasterHeight = Math.Max(
            1,
            (int)MathF.Round(stage.Height * resolvedRasterScale));

        var objectIndices = new HashSet<int>(renderItems.Count);
        var preparedByItem = ArrayPool<Reference3DCpuRasterCommand>.Shared.Rent(
            Math.Max(1, renderItems.Count));
        Array.Clear(preparedByItem, 0, renderItems.Count);
        var preparationWorkers = ParallelBatch.WorkerCount(renderItems.Count, 16, maxWorkers);
        string? preparationFailureReason = null;
        var preparationStarted = Stopwatch.GetTimestamp();
        try
        {
            ParallelBatch.For(
                renderItems.Count,
                16,
                (_, start, end) =>
                {
                    // These caches are local to each worker so command
                    // preparation remains lock-free while reusing transforms.
                    var sourceToScreenCache = new Dictionary<int, Matrix3x2>(8);
                    var sourceToScreenFailures = new HashSet<int>();
                    var geometryCache = new Dictionary<
                        (Reference3DProjectedContour[] Contours, bool Fill),
                        Reference3DCpuRasterGeometry>(8);
                    var gradientCache = new Dictionary<
                        GradientStop[],
                        Reference3DCpuRasterGradientProfile>(
                            8,
                            GradientStopProfileComparer.Instance);
                    var linearGradientCache = new Dictionary<
                        int,
                        Reference3DCpuRasterLinearGradientProjection>(8);
                    for (var itemIndex = start; itemIndex < end; itemIndex++)
                    {
                        var item = renderItems[itemIndex];
                        if ((uint)item.ObjectIndex >= stage.Scene.ObjectCount
                            || stage.IsObjectHiddenForRendering(stage.Scene, item.ObjectIndex))
                        {
                            continue;
                        }

                        if (!TryCreateReference3DCpuRasterCommand(
                                 stage,
                                 item,
                                 resolvedRasterScale,
                                 rasterHeight,
                                 buildFillScanlines,
                                 buildGradientLookup,
                                 sourceToScreenCache,
                                sourceToScreenFailures,
                                geometryCache,
                                gradientCache,
                                linearGradientCache,
                                out var command,
                                out var failureReason))
                        {
                            if (Reference3DCpuRasterFailureIsInvisible(failureReason))
                            {
                                // A perspective projection may collapse a
                                // tiny fragment to zero area, which D2D also
                                // omits from the final image.
                                continue;
                            }

                            Interlocked.CompareExchange(
                                ref preparationFailureReason,
                                failureReason,
                                null);
                            continue;
                        }

                        preparedByItem[itemIndex] = command;
                    }
                },
                maxWorkers);
            if (preparationFailureReason is { Length: > 0 } failureReason)
            {
                LastReference3DCpuRasterFallbackReason = failureReason;
                return false;
            }

            var preparedCount = 0;
            for (var itemIndex = 0; itemIndex < renderItems.Count; itemIndex++)
            {
                if (preparedByItem[itemIndex] is not { } command) continue;
                preparedByItem[preparedCount++] = command;
                objectIndices.Add(renderItems[itemIndex].ObjectIndex);
            }

            if (preparedCount < Reference3DCpuRasterMinimumCommands)
            {
                LastReference3DCpuRasterFallbackReason = "too_few_commands";
                return false;
            }
            commands = new Reference3DCpuRasterCommand[preparedCount];
            Array.Copy(preparedByItem, commands, preparedCount);
        }
        catch
        {
            LastReference3DCpuRasterFallbackReason = "command_preparation_exception";
            return false;
        }
        finally
        {
            ArrayPool<Reference3DCpuRasterCommand>.Shared.Return(
                preparedByItem,
                clearArray: true);
            LastReference3DCpuRasterPreparationWorkers = preparationWorkers;
            LastReference3DCpuRasterPreparationMilliseconds =
                Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds;
        }

        drawnObjects = objectIndices.Count;
        return true;
    }

    private bool DrawReference3DCpuRasterToGdi(
        Graphics graphics,
        int[] pixels,
        int width,
        int height,
        int destinationWidth,
        int destinationHeight)
    {
        var bitmap = EnsureReference3DFastPlaybackBitmap(width, height);
        var bounds = new Rectangle(0, 0, width, height);
        var bitmapWriteStarted = Stopwatch.GetTimestamp();
        var bitmapData = bitmap.LockBits(
            bounds,
            ImageLockMode.WriteOnly,
            GdiPixelFormat.Format32bppPArgb);
        try
        {
            for (var row = 0; row < height; row++)
            {
                var bitmapRow = bitmapData.Stride >= 0 ? row : height - row - 1;
                Marshal.Copy(
                    pixels,
                    row * width,
                    IntPtr.Add(bitmapData.Scan0, bitmapRow * bitmapData.Stride),
                    width);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
        LastReference3DPlaybackBitmapWriteMilliseconds =
            Stopwatch.GetElapsedTime(bitmapWriteStarted).TotalMilliseconds;

        // The playback bitmap is already composited and opaque. A native GDI
        // copy avoids GDI+'s per-frame image conversion on a real HWND.
        graphics.ResetTransform();
        graphics.ResetClip();
        _reference3DFastPlaybackBitmapGraphics!.ResetTransform();
        _reference3DFastPlaybackBitmapGraphics.ResetClip();
        var blitStarted = Stopwatch.GetTimestamp();
        var destinationHdc = IntPtr.Zero;
        var sourceHdc = IntPtr.Zero;
        try
        {
            sourceHdc = _reference3DFastPlaybackBitmapGraphics.GetHdc();
            destinationHdc = graphics.GetHdc();
            if (!TryBlitReference3DPlaybackBitmap(
                    destinationHdc,
                    sourceHdc,
                    width,
                    height,
                    destinationWidth,
                    destinationHeight))
            {
                LastReference3DCpuRasterFallbackReason = "gdi_blit_failed";
                return false;
            }
        }
        finally
        {
            if (destinationHdc != IntPtr.Zero) graphics.ReleaseHdc(destinationHdc);
            if (sourceHdc != IntPtr.Zero)
            {
                _reference3DFastPlaybackBitmapGraphics.ReleaseHdc(sourceHdc);
            }
            LastReference3DPlaybackBlitMilliseconds =
                Stopwatch.GetElapsedTime(blitStarted).TotalMilliseconds;
        }

        return true;
    }

    private Bitmap EnsureReference3DFastPlaybackBitmap(int width, int height)
    {
        if (_reference3DFastPlaybackBitmap is not null
            && _reference3DFastPlaybackBitmapWidth == width
            && _reference3DFastPlaybackBitmapHeight == height)
        {
            return _reference3DFastPlaybackBitmap;
        }

        _reference3DFastPlaybackBitmapGraphics?.Dispose();
        _reference3DFastPlaybackBitmapGraphics = null;
        _reference3DFastPlaybackBitmap?.Dispose();
        _reference3DFastPlaybackBitmap = new Bitmap(
            width,
            height,
            GdiPixelFormat.Format32bppPArgb);
        _reference3DFastPlaybackBitmapGraphics = Graphics.FromImage(
            _reference3DFastPlaybackBitmap);
        _reference3DFastPlaybackBitmapWidth = width;
        _reference3DFastPlaybackBitmapHeight = height;
        return _reference3DFastPlaybackBitmap;
    }

    private void CopyFastPlaybackBackgroundToCpuPixels(
        int[] pixels,
        int width,
        int height)
    {
        var backgroundPixels = _reference3DPlaybackBackgroundPixels;
        var pixelCount = checked(width * height);
        if (backgroundPixels is null || backgroundPixels.Length < pixelCount)
        {
            throw new InvalidOperationException("Fast playback background pixels are not ready.");
        }

        var copyStarted = Stopwatch.GetTimestamp();
        Array.Copy(backgroundPixels, pixels, pixelCount);
        LastReference3DPlaybackBackgroundReadbackMilliseconds =
            Stopwatch.GetElapsedTime(copyStarted).TotalMilliseconds;
    }

    private static void CopyReference3DPlaybackBitmapToPixels(
        Bitmap bitmap,
        int[] pixels,
        int width,
        int height)
    {
        var bounds = new Rectangle(0, 0, width, height);
        var bitmapData = bitmap.LockBits(
            bounds,
            ImageLockMode.ReadOnly,
            GdiPixelFormat.Format32bppPArgb);
        try
        {
            for (var row = 0; row < height; row++)
            {
                var bitmapRow = bitmapData.Stride >= 0 ? row : height - row - 1;
                Marshal.Copy(
                    IntPtr.Add(bitmapData.Scan0, bitmapRow * bitmapData.Stride),
                    pixels,
                    row * width,
                    width);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private static bool Reference3DCpuRasterFailureIsInvisible(string failureReason)
    {
        return failureReason is "empty_contours" or "empty_stroke_segments";
    }

    private bool CanUseReference3DCpuRaster(
        StageControl stage,
        IReadOnlyList<Reference3DRenderItem> renderItems)
    {
        if (Environment.GetEnvironmentVariable("VECTOR_DISABLE_CPU_RASTER") == "1")
        {
            LastReference3DCpuRasterFallbackReason = "disabled_by_environment";
            return false;
        }
        if (renderItems.Count < Reference3DCpuRasterMinimumCommands
            || stage.Scene.HasNonNormalLayerBlendModes
            || stage.SceneHasDistortionsForRendering(stage.Scene)
            || stage.HasSceneCompositionMaskClips(stage.Scene))
        {
            LastReference3DCpuRasterFallbackReason = renderItems.Count < Reference3DCpuRasterMinimumCommands
                ? "too_few_items"
                : stage.Scene.HasNonNormalLayerBlendModes
                    ? "non_normal_blend"
                    : stage.SceneHasDistortionsForRendering(stage.Scene)
                        ? "distortion"
                        : "composition_mask";
            return false;
        }

        foreach (var item in renderItems)
        {
            if ((uint)item.ObjectIndex >= stage.Scene.ObjectCount
                || stage.IsObjectHiddenForRendering(stage.Scene, item.ObjectIndex))
            {
                continue;
            }

            var isFrontFill = item.Kind == Reference3DRenderKind.FrontFill;
            var isFrontStroke = item.Kind == Reference3DRenderKind.FrontStroke;
            var isIntersectionEdge = item.Kind == Reference3DRenderKind.IntersectionEdge;
            if (!isFrontFill && !isFrontStroke && !isIntersectionEdge
                || item.FragmentClip is not null
                || item.OcclusionContours is not null
                || item.OpticalSurface is not null
                || !isIntersectionEdge && item.SecondaryObjectIndex >= 0
                || float.IsFinite(item.MaterialOpacity) && item.MaterialOpacity < 0.999999f
                || item.LocalLightLayers is { Length: > 0 }
                || item.ShadowLayers is { Length: > 0 }
                || item.OpticalResponse != Reference3DOpticalResponse.Identity)
            {
                LastReference3DCpuRasterFallbackReason = "unsupported_render_item";
                return false;
            }

            if ((uint)item.LayerIndex >= stage.Scene.LayerCount
                || stage.Scene.GetLayerKind(item.LayerIndex) != DrawingLayerKind.Drawing
                || stage.Scene.TryGetMaskLayerIndex(item.LayerIndex, out _))
            {
                LastReference3DCpuRasterFallbackReason = "unsupported_layer";
                return false;
            }

            if (isIntersectionEdge)
            {
                if (!float.IsFinite(item.EdgeWidth)
                    || item.EdgeWidth <= 0
                    || GdiColor.FromArgb(item.EdgeArgb).A == 0)
                {
                    LastReference3DCpuRasterFallbackReason = "invalid_intersection_edge";
                    return false;
                }
                continue;
            }

            var shape = stage.Scene.ShapeKind[item.ObjectIndex];
            if (shape is ShapeKind.Line
                or ShapeKind.Freeform
                or ShapeKind.BrushStroke
                or ShapeKind.ImportedSvg
                or ShapeKind.Text
                or ShapeKind.MixingStroke)
            {
                LastReference3DCpuRasterFallbackReason = "unsupported_shape";
                return false;
            }

            if (shape == ShapeKind.Path
                && stage.Scene.TryGetPathBezierLocalContours(item.ObjectIndex, out var bezierContours)
                && bezierContours.Length > 0)
            {
                LastReference3DCpuRasterFallbackReason = "bezier_path";
                return false;
            }

        }

        return true;
    }

    private bool TryCreateReference3DCpuRasterCommand(
        StageControl stage,
        Reference3DRenderItem item,
        float rasterScale,
        int rasterHeight,
        bool buildFillScanlines,
        bool buildGradientLookup,
        Dictionary<int, Matrix3x2> sourceToScreenCache,
        HashSet<int> sourceToScreenFailures,
        Dictionary<
            (Reference3DProjectedContour[] Contours, bool Fill),
            Reference3DCpuRasterGeometry> geometryCache,
        Dictionary<GradientStop[], Reference3DCpuRasterGradientProfile> gradientCache,
        Dictionary<int, Reference3DCpuRasterLinearGradientProjection> linearGradientCache,
        out Reference3DCpuRasterCommand command,
        out string failureReason)
    {
        command = null!;
        failureReason = "unknown_command_failure";
        var scene = stage.Scene;
        var objectIndex = item.ObjectIndex;
        var fill = item.Kind == Reference3DRenderKind.FrontFill;
        var intersectionEdge = item.Kind == Reference3DRenderKind.IntersectionEdge;
        if (!TryGetReference3DCpuRasterGeometry(
                item.Contours,
                fill,
                rasterScale,
                geometryCache,
                out var geometry,
                out failureReason))
        {
            return false;
        }

        var minX = geometry.MinX;
        var minY = geometry.MinY;
        var maxX = geometry.MaxX;
        var maxY = geometry.MaxY;
        var segments = geometry.Segments;
        if (!fill && segments.Length == 0)
        {
            failureReason = "empty_stroke_segments";
            return false;
        }

        var solidArgb = fill
            ? item.VectorLightingArgb ?? scene.Argb[objectIndex]
            : intersectionEdge
                ? item.VectorLightingArgb
                    ?? item.SolidStrokeOpticalBaseArgb
                    ?? item.EdgeArgb
                : item.SolidStrokeOpticalBaseArgb
                    ?? (scene.StrokeArgb.Length > objectIndex
                        ? scene.StrokeArgb[objectIndex]
                        : GdiColor.FromArgb(238, 242, 241).ToArgb());
        var strokeWidth = 0f;
        if (!fill)
        {
            strokeWidth = intersectionEdge
                ? item.EdgeWidth
                : stage.GetReference3DStrokeWidth(
                    objectIndex,
                    scene.Stroke[objectIndex]);
            if (!float.IsFinite(strokeWidth) || strokeWidth <= 0)
            {
                failureReason = "invalid_stroke_width";
                return false;
            }
            strokeWidth *= rasterScale;
            var padding = strokeWidth * 0.5f + 1f;
            minX -= padding;
            minY -= padding;
            maxX += padding;
            maxY += padding;
        }

        var gradientKind = GradientKind.Solid;
        var gradientStops = Array.Empty<Reference3DCpuRasterGradientStop>();
        var gradientStartScreen = default(Vector2);
        var gradientEndScreen = default(Vector2);
        var gradientStartSource = default(Vector2);
        var gradientRadiusSquared = 0f;
        var screenToSource = Matrix3x2.Identity;
        var gradientStopsOpaque = true;
        var gradientLookup = Array.Empty<int>();
        var opticalGradientStops = fill
            ? item.OpticalGradientStops
            : item.OpticalStrokeGradientStops;
        var hasGradient = !intersectionEdge
            && (opticalGradientStops is { Length: > 0 } || scene.HasGradient(objectIndex));
        if (hasGradient)
        {
            gradientKind = scene.GetGradientKind(objectIndex);
            if (gradientKind is not (GradientKind.Linear or GradientKind.Radial)
                || scene.HasGradientPath(objectIndex))
            {
                failureReason = "unsupported_gradient_kind";
                return false;
            }

            GradientStop[] stops;
            if (opticalGradientStops is { Length: > 0 } suppliedStops)
            {
                stops = suppliedStops;
            }
            else if (!scene.TryGetGradientStopsForRendering(objectIndex, out stops))
            {
                stops = [];
            }
            if (stops.Length == 0)
            {
                failureReason = "gradient_without_stops";
                return false;
            }
            var gradientProfile = GetReference3DCpuRasterGradientProfile(
                stops,
                gradientCache,
                buildGradientLookup);
            gradientStops = gradientProfile.Stops;
            gradientLookup = gradientProfile.Lookup;
            gradientStopsOpaque = gradientProfile.Opaque;
            var sourceStart = scene.GetGradientStart(objectIndex);
            var sourceEnd = scene.GetGradientEnd(objectIndex);
            gradientStartSource = new Vector2(sourceStart.X, sourceStart.Y);
            if (gradientKind == GradientKind.Linear)
            {
                if (!TryGetCachedReference3DCpuRasterLinearGradientAxis(
                        stage,
                        objectIndex,
                        sourceStart,
                        sourceEnd,
                        linearGradientCache,
                        out gradientStartScreen,
                        out gradientEndScreen,
                        out var useSolidGradientFallback))
                {
                    failureReason = "gradient_transform";
                    return false;
                }

                if (useSolidGradientFallback)
                {
                    // Keep this one command on the dense path when the full
                    // authored axis is behind the near plane. A solid midpoint
                    // is a bounded visual fallback; rejecting the command
                    // would reject every other fragment in the frame.
                    solidArgb = SampleGradientArgb(gradientStops, 0.5f);
                    gradientKind = GradientKind.Solid;
                    gradientStops = Array.Empty<Reference3DCpuRasterGradientStop>();
                }
            }
            else
            {
                if (!TryGetReference3DCpuRasterSourceToScreen(
                        stage,
                        objectIndex,
                        sourceToScreenCache,
                        sourceToScreenFailures,
                        out var sourceToScreen)
                    || !Matrix3x2.Invert(sourceToScreen, out screenToSource))
                {
                    failureReason = "gradient_transform";
                    return false;
                }

                gradientStartScreen = Vector2.Transform(gradientStartSource, sourceToScreen);
                gradientEndScreen = Vector2.Transform(
                    new Vector2(sourceEnd.X, sourceEnd.Y),
                    sourceToScreen);
            }
            if (!IsFinite(gradientStartScreen) || !IsFinite(gradientEndScreen))
            {
                failureReason = "non_finite_gradient";
                return false;
            }
            gradientStartScreen *= rasterScale;
            gradientEndScreen *= rasterScale;
            var sourceAxis = new Vector2(sourceEnd.X - sourceStart.X, sourceEnd.Y - sourceStart.Y);
            gradientRadiusSquared = sourceAxis.LengthSquared();
            if (!float.IsFinite(gradientRadiusSquared) || gradientRadiusSquared <= 0.000001f)
            {
                gradientKind = GradientKind.Solid;
                solidArgb = gradientStops[^1].Argb;
                gradientStops = Array.Empty<Reference3DCpuRasterGradientStop>();
            }
        }
        else if (opticalGradientStops is { Length: > 0 })
        {
            failureReason = "unsupported_optical_gradient";
            return false;
        }

        var fillScanlineStartY = 0;
        var fillScanlineOffsets = Array.Empty<int>();
        var fillScanlineSpans = Array.Empty<Reference3DCpuRasterSpan>();
        if (fill && buildFillScanlines)
        {
            BuildReference3DCpuFillScanlines(
                segments,
                minY,
                maxY,
                rasterHeight,
                out fillScanlineStartY,
                out fillScanlineOffsets,
                out fillScanlineSpans);
        }

        command = new Reference3DCpuRasterCommand(
            fill,
            geometry.Contours,
            segments,
            minX,
            minY,
            maxX,
            maxY,
            solidArgb,
            strokeWidth,
            gradientKind,
            gradientStops,
            gradientLookup,
            gradientStartScreen,
            gradientEndScreen,
            gradientStartSource,
            gradientRadiusSquared,
            screenToSource,
            rasterScale,
            geometry.PointCount,
            rasterHeight,
            gradientStopsOpaque,
            geometry.SingleClosedContour,
            fillScanlineStartY,
            fillScanlineOffsets,
            fillScanlineSpans,
            fillScanlinesReady: !fill || buildFillScanlines);
        return true;
    }

    private static void BuildReference3DCpuFillScanlines(
        Reference3DCpuRasterSegment[] segments,
        float minY,
        float maxY,
        int rasterHeight,
        out int startY,
        out int[] rowOffsets,
        out Reference3DCpuRasterSpan[] spans)
    {
        startY = Math.Clamp((int)MathF.Floor(minY), 0, rasterHeight);
        var endY = Math.Clamp((int)MathF.Ceiling(maxY), startY, rasterHeight);
        var rowCount = endY - startY;
        rowOffsets = new int[rowCount + 1];
        if (rowCount == 0 || segments.Length == 0)
        {
            spans = Array.Empty<Reference3DCpuRasterSpan>();
            return;
        }

        var intersectionBuffer = ArrayPool<float>.Shared.Rent(segments.Length);
        var spanList = new List<Reference3DCpuRasterSpan>(
            Math.Min(segments.Length * 2, rowCount * 2));
        try
        {
            for (var row = 0; row < rowCount; row++)
            {
                var y = startY + row;
                AppendReference3DCpuFillSpans(
                    segments,
                    y + 0.25f,
                    intersectionBuffer,
                    spanList);
                AppendReference3DCpuFillSpans(
                    segments,
                    y + 0.75f,
                    intersectionBuffer,
                    spanList);
                rowOffsets[row + 1] = spanList.Count;
            }

            spans = spanList.ToArray();
        }
        finally
        {
            ArrayPool<float>.Shared.Return(intersectionBuffer, clearArray: false);
        }
    }

    private static void AppendReference3DCpuFillSpans(
        Reference3DCpuRasterSegment[] segments,
        float sampleY,
        float[] intersections,
        ICollection<Reference3DCpuRasterSpan> spans)
    {
        var intersectionCount = 0;
        for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
        {
            ref readonly var segment = ref segments[segmentIndex];
            if (segment.InverseDy == 0f
                || (segment.A.Y > sampleY) == (segment.B.Y > sampleY))
            {
                continue;
            }

            if (intersectionCount >= intersections.Length) return;
            var amount = (sampleY - segment.A.Y) * segment.InverseDy;
            intersections[intersectionCount++] = segment.A.X + segment.Dx * amount;
        }

        if (intersectionCount <= 0) return;
        Array.Sort(intersections, 0, intersectionCount);
        for (var index = 1; index < intersectionCount; index += 2)
        {
            var start = intersections[index - 1];
            var end = intersections[index];
            if (float.IsFinite(start) && float.IsFinite(end) && end > start)
            {
                spans.Add(new Reference3DCpuRasterSpan(start, end));
            }
        }
    }

    private static bool TryGetReference3DCpuRasterGeometry(
        Reference3DProjectedContour[] projectedContours,
        bool fill,
        float rasterScale,
        Dictionary<
            (Reference3DProjectedContour[] Contours, bool Fill),
            Reference3DCpuRasterGeometry> cache,
        out Reference3DCpuRasterGeometry geometry,
        out string failureReason)
    {
        var key = (projectedContours, fill);
        if (cache.TryGetValue(key, out geometry))
        {
            failureReason = string.Empty;
            return true;
        }

        var contours = new GdiPointF[projectedContours.Length][];
        var contourCount = 0;
        var pointCount = 0;
        var segmentCapacity = 0;
        var minX = float.PositiveInfinity;
        var minY = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var maxY = float.NegativeInfinity;
        var singleClosedContour = projectedContours.Length == 1
            && projectedContours[0].Closed;
        for (var contourIndex = 0; contourIndex < projectedContours.Length; contourIndex++)
        {
            var contour = projectedContours[contourIndex];
            if (fill && !contour.Closed) continue;
            var points = contour.Points;
            if (points.Length < (contour.Closed ? 3 : 2)) continue;

            var copy = points;
            if (rasterScale < 0.999999f)
            {
                var scaled = new GdiPointF[copy.Length];
                for (var pointIndex = 0; pointIndex < copy.Length; pointIndex++)
                {
                    scaled[pointIndex] = new GdiPointF(
                        copy[pointIndex].X * rasterScale,
                        copy[pointIndex].Y * rasterScale);
                }
                copy = scaled;
            }

            for (var pointIndex = 0; pointIndex < copy.Length; pointIndex++)
            {
                var point = copy[pointIndex];
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                {
                    geometry = default;
                    failureReason = "non_finite_contour";
                    return false;
                }
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }

            contours[contourCount++] = copy;
            pointCount = checked(pointCount + copy.Length);
            segmentCapacity = checked(segmentCapacity
                + (contour.Closed ? copy.Length : copy.Length - 1));
        }

        if (contourCount == 0
            || !float.IsFinite(minX)
            || !float.IsFinite(minY)
            || !float.IsFinite(maxX)
            || !float.IsFinite(maxY)
            || maxX <= minX
            || maxY <= minY)
        {
            geometry = default;
            failureReason = "empty_contours";
            return false;
        }

        if (contourCount != contours.Length)
        {
            Array.Resize(ref contours, contourCount);
        }

        var segments = new Reference3DCpuRasterSegment[segmentCapacity];
        var segmentCount = 0;
        var outputContourIndex = 0;
        for (var contourIndex = 0; contourIndex < projectedContours.Length; contourIndex++)
        {
            var contour = projectedContours[contourIndex];
            if (fill && !contour.Closed) continue;
            var points = contour.Points;
            if (points.Length < (contour.Closed ? 3 : 2)) continue;
            var copy = contours[outputContourIndex++];
            var segmentCountForContour = contour.Closed ? copy.Length : copy.Length - 1;
            for (var index = 0; index < segmentCountForContour; index++)
            {
                var next = (index + 1) % copy.Length;
                var first = copy[index];
                var second = copy[next];
                if (first.Equals(second)) continue;
                segments[segmentCount++] = new Reference3DCpuRasterSegment(first, second);
            }
        }

        if (segmentCount != segments.Length)
        {
            Array.Resize(ref segments, segmentCount);
        }

        geometry = new Reference3DCpuRasterGeometry(
            contours,
            segments,
            minX,
            minY,
            maxX,
            maxY,
            pointCount,
            singleClosedContour);
        cache.Add(key, geometry);
        failureReason = string.Empty;
        return true;
    }

    private static Reference3DCpuRasterGradientProfile
        GetReference3DCpuRasterGradientProfile(
            GradientStop[] stops,
            Dictionary<GradientStop[], Reference3DCpuRasterGradientProfile> cache,
            bool buildLookup)
    {
        if (cache.TryGetValue(stops, out var profile))
        {
            if (!buildLookup || profile.Lookup.Length > 0) return profile;
            var cachedLookup = CreateReference3DCpuRasterGradientLookup(profile.Stops);
            profile = profile with { Lookup = cachedLookup };
            cache[stops] = profile;
            return profile;
        }

        var converted = CreateReference3DCpuRasterGradientStops(stops);
        var opaque = true;
        for (var index = 0; index < converted.Length; index++)
        {
            if (converted[index].A != 255)
            {
                opaque = false;
                break;
            }
        }

        var lookup = buildLookup
            ? CreateReference3DCpuRasterGradientLookup(converted)
            : Array.Empty<int>();

        profile = new Reference3DCpuRasterGradientProfile(converted, lookup, opaque);
        cache.Add(stops, profile);
        return profile;
    }

    private static int[] CreateReference3DCpuRasterGradientLookup(
        Reference3DCpuRasterGradientStop[] stops)
    {
        var lookup = new int[Reference3DCpuRasterGradientLookupSize];
        for (var index = 0; index < lookup.Length; index++)
        {
            lookup[index] = SampleGradientArgb(
                stops,
                index / (float)(lookup.Length - 1));
        }
        return lookup;
    }

    private static bool TryGetCachedReference3DCpuRasterLinearGradientAxis(
        StageControl stage,
        int objectIndex,
        GdiPointF sourceStart,
        GdiPointF sourceEnd,
        Dictionary<int, Reference3DCpuRasterLinearGradientProjection> cache,
        out Vector2 gradientStartScreen,
        out Vector2 gradientEndScreen,
        out bool useSolidFallback)
    {
        if (cache.TryGetValue(objectIndex, out var cached))
        {
            gradientStartScreen = cached.Start;
            gradientEndScreen = cached.End;
            useSolidFallback = cached.UseSolidFallback;
            return cached.Succeeded;
        }

        var succeeded = TryGetReference3DCpuRasterLinearGradientAxis(
            stage,
            objectIndex,
            sourceStart,
            sourceEnd,
            out gradientStartScreen,
            out gradientEndScreen,
            out useSolidFallback);
        cache.Add(
            objectIndex,
            new Reference3DCpuRasterLinearGradientProjection(
                succeeded,
                useSolidFallback,
                gradientStartScreen,
                gradientEndScreen));
        return succeeded;
    }

    private static bool TryGetReference3DCpuRasterLinearGradientAxis(
        StageControl stage,
        int objectIndex,
        GdiPointF sourceStart,
        GdiPointF sourceEnd,
        out Vector2 gradientStartScreen,
        out Vector2 gradientEndScreen,
        out bool useSolidFallback)
    {
        gradientStartScreen = default;
        gradientEndScreen = default;
        useSolidFallback = false;

        var firstPoint = default(Vector2);
        var secondPoint = default(Vector2);
        var firstT = 0f;
        var secondT = 0f;
        var hasFirstPoint = false;
        var hasSecondPoint = false;

        if (stage.TryProjectReference3DFrontPoint(
                objectIndex,
                sourceStart,
                out var projectedStart,
                out _))
        {
            firstPoint = new Vector2(projectedStart.X, projectedStart.Y);
            firstT = 0f;
            hasFirstPoint = IsFinite(firstPoint);
        }

        if (stage.TryProjectReference3DFrontPoint(
                objectIndex,
                sourceEnd,
                out var projectedEnd,
                out _))
        {
            var endpoint = new Vector2(projectedEnd.X, projectedEnd.Y);
            if (IsFinite(endpoint))
            {
                if (!hasFirstPoint)
                {
                    firstPoint = endpoint;
                    firstT = 1f;
                    hasFirstPoint = true;
                }
                else if (firstT != 1f)
                {
                    secondPoint = endpoint;
                    secondT = 1f;
                    hasSecondPoint = true;
                }
            }
        }

        // The projected axis can cross the perspective near plane. Sampling
        // its interior supplies a stable local projective approximation even
        // when one or both authored endpoints are not projectable.
        const int InteriorSampleCount = 9;
        for (var sampleIndex = 0;
             sampleIndex < InteriorSampleCount && !hasSecondPoint;
             sampleIndex++)
        {
            var t = 0.125f + sampleIndex * (0.75f / (InteriorSampleCount - 1));
            var sourcePoint = new GdiPointF(
                sourceStart.X + (sourceEnd.X - sourceStart.X) * t,
                sourceStart.Y + (sourceEnd.Y - sourceStart.Y) * t);
            if (!stage.TryProjectReference3DFrontPoint(
                    objectIndex,
                    sourcePoint,
                    out var projected,
                    out _))
            {
                continue;
            }

            var point = new Vector2(projected.X, projected.Y);
            if (!IsFinite(point)) continue;
            if (!hasFirstPoint)
            {
                firstPoint = point;
                firstT = t;
                hasFirstPoint = true;
                continue;
            }

            if (Math.Abs(t - firstT) > 0.0001f)
            {
                secondPoint = point;
                secondT = t;
                hasSecondPoint = true;
            }
        }

        if (!hasFirstPoint || !hasSecondPoint)
        {
            useSolidFallback = true;
            return true;
        }

        var deltaT = secondT - firstT;
        if (!float.IsFinite(deltaT) || Math.Abs(deltaT) <= 0.0001f)
        {
            useSolidFallback = true;
            return true;
        }

        var projectedDelta = secondPoint - firstPoint;
        var startFactor = -firstT / deltaT;
        var endFactor = (1f - firstT) / deltaT;
        gradientStartScreen = firstPoint + projectedDelta * startFactor;
        gradientEndScreen = firstPoint + projectedDelta * endFactor;
        if (!IsFinite(gradientStartScreen) || !IsFinite(gradientEndScreen))
        {
            useSolidFallback = true;
            gradientStartScreen = default;
            gradientEndScreen = default;
        }

        return true;
    }

    private static float GetReference3DCpuRasterScale(
        StageControl stage,
        int renderItemCount)
    {
        return stage.Reference3DPlaybackActive
            && renderItemCount >= Reference3DCpuRasterDynamicResolutionCommandThreshold
            ? Reference3DCpuRasterDynamicResolutionScale
            : 1f;
    }

    private static float GetReference3DPlaybackRasterScale(int renderItemCount)
    {
        return renderItemCount >= Reference3DCpuRasterDynamicResolutionCommandThreshold
            ? Reference3DCpuRasterDynamicResolutionScale
            : 1f;
    }

    private static Reference3DCpuRasterTileBins?
        TryBuildReference3DCpuRasterTileBins(
            Reference3DCpuRasterCommand[] commands,
            int targetWidth,
            int targetHeight,
            float rasterScale)
    {
        if (commands.Length == 0
            || targetWidth <= 0
            || targetHeight <= 0
            || !float.IsFinite(rasterScale)
            || rasterScale <= 0f)
        {
            return null;
        }

        try
        {
            var width = Math.Max(1, (int)MathF.Round(targetWidth * rasterScale));
            var height = Math.Max(1, (int)MathF.Round(targetHeight * rasterScale));
            var tilesX = (width + Reference3DCpuRasterTileSize - 1)
                / Reference3DCpuRasterTileSize;
            var tilesY = (height + Reference3DCpuRasterTileSize - 1)
                / Reference3DCpuRasterTileSize;
            var tileCount = checked(tilesX * tilesY);
            var tileCounts = new int[tileCount];
            var commandOffsets = new int[tileCount + 1];
            var commandTileBounds = new int[checked(commands.Length * 4)];
            if (!TryBuildReference3DCpuRasterCommandBinsCore(
                    commands,
                    width,
                    height,
                    tilesX,
                    tilesY,
                    tileCounts,
                    commandOffsets,
                    commandTileBounds,
                    out var commandIndices))
            {
                return null;
            }

            return new Reference3DCpuRasterTileBins(
                commands,
                width,
                height,
                tilesX,
                tilesY,
                commandOffsets,
                commandIndices);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetReference3DCpuRasterSourceToScreen(
        StageControl stage,
        int objectIndex,
        Dictionary<int, Matrix3x2> cache,
        HashSet<int> failures,
        out Matrix3x2 transform)
    {
        if (cache.TryGetValue(objectIndex, out transform)) return true;
        if (failures.Contains(objectIndex))
        {
            transform = default;
            return false;
        }

        if (stage.TryGetReference3DFlatToScreenTransform(objectIndex, out transform))
        {
            cache[objectIndex] = transform;
            return true;
        }

        // A perspective projection is locally projective. The existing mesh
        // builder supplies a bounded affine approximation for small planar
        // surfaces, which is sufficient for material sampling while the
        // projected contours still provide the exact silhouette.
        var succeeded = stage.TryGetReference3DProjectiveMesh(
                objectIndex,
                usePrimaryContourQuad: false,
                out var triangles)
            && StageControl.TryGetReference3DProjectiveAffineTransform(
                triangles,
                out transform);
        if (succeeded) cache[objectIndex] = transform;
        else failures.Add(objectIndex);
        return succeeded;
    }

    private static Reference3DCpuRasterGradientStop[] CreateReference3DCpuRasterGradientStops(
        IReadOnlyList<GradientStop> stops)
    {
        var result = new Reference3DCpuRasterGradientStop[stops.Count];
        for (var index = 0; index < stops.Count; index++)
        {
            var argb = stops[index].Argb;
            result[index] = new Reference3DCpuRasterGradientStop(
                stops[index].Position,
                (byte)(argb >> 24),
                (byte)(argb >> 16),
                (byte)(argb >> 8),
                (byte)argb);
        }
        return result;
    }

    private int[] EnsureReference3DCpuRasterPixels(int pixelCount)
    {
        if (_reference3DCpuRasterPixels is null || _reference3DCpuRasterPixels.Length < pixelCount)
        {
            _reference3DCpuRasterPixels = new int[pixelCount];
        }
        return _reference3DCpuRasterPixels;
    }

    private bool TryBuildReference3DCpuRasterCommandBins(
        Reference3DCpuRasterCommand[] commands,
        int width,
        int height,
        int tilesX,
        int tilesY,
        out int[] tileCommandOffsets,
        out int[] tileCommandIndices)
    {
        tileCommandOffsets = Array.Empty<int>();
        tileCommandIndices = Array.Empty<int>();
        try
        {
            var tileCount = checked(tilesX * tilesY);
            var tileCounts = EnsureReference3DCpuRasterTileCommandCounts(tileCount);
            tileCommandOffsets = EnsureReference3DCpuRasterTileCommandOffsets(tileCount + 1);
            var commandTileBounds = EnsureReference3DCpuRasterCommandTileBounds(commands.Length);
            if (!TryBuildReference3DCpuRasterCommandBinsCore(
                    commands,
                    width,
                    height,
                    tilesX,
                    tilesY,
                    tileCounts,
                    tileCommandOffsets,
                    commandTileBounds,
                    out var indices))
            {
                return false;
            }

            tileCommandIndices = EnsureReference3DCpuRasterCommandIndices(indices.Length);
            Array.Copy(indices, tileCommandIndices, indices.Length);
            return true;
        }
        catch
        {
            tileCommandOffsets = Array.Empty<int>();
            tileCommandIndices = Array.Empty<int>();
            return false;
        }
    }

    private static bool TryBuildReference3DCpuRasterCommandBinsCore(
        Reference3DCpuRasterCommand[] commands,
        int width,
        int height,
        int tilesX,
        int tilesY,
        int[] tileCounts,
        int[] tileCommandOffsets,
        int[] commandTileBounds,
        out int[] tileCommandIndices)
    {
        tileCommandIndices = Array.Empty<int>();
        try
        {
            var tileCount = checked(tilesX * tilesY);
            if (tileCounts.Length < tileCount
                || tileCommandOffsets.Length < tileCount + 1
                || commandTileBounds.Length < checked(commands.Length * 4))
            {
                return false;
            }

            Array.Clear(tileCounts, 0, tileCount);

            long binEntryCount = 0;
            for (var commandIndex = 0; commandIndex < commands.Length; commandIndex++)
            {
                var command = commands[commandIndex];
                if (!TryGetReference3DCpuRasterTileRange(
                        command,
                        width,
                        height,
                        tilesX,
                        tilesY,
                        out var startX,
                        out var startY,
                        out var endX,
                        out var endY,
                        out var intersectsViewport))
                {
                    return false;
                }

                var boundsOffset = checked(commandIndex * 4);
                commandTileBounds[boundsOffset] = startX;
                commandTileBounds[boundsOffset + 1] = startY;
                commandTileBounds[boundsOffset + 2] = endX;
                commandTileBounds[boundsOffset + 3] = endY;
                if (!intersectsViewport) continue;

                for (var tileY = startY; tileY < endY; tileY++)
                {
                    var tileIndex = tileY * tilesX + startX;
                    for (var tileX = startX; tileX < endX; tileX++, tileIndex++)
                    {
                        if (++binEntryCount > Reference3DCpuRasterMaximumBinEntries)
                        {
                            return false;
                        }

                        tileCounts[tileIndex]++;
                    }
                }
            }

            tileCommandOffsets[0] = 0;
            for (var tileIndex = 0; tileIndex < tileCount; tileIndex++)
            {
                var nextOffset = (long)tileCommandOffsets[tileIndex] + tileCounts[tileIndex];
                if (nextOffset > Reference3DCpuRasterMaximumBinEntries
                    || nextOffset > int.MaxValue)
                {
                    return false;
                }

                tileCommandOffsets[tileIndex + 1] = (int)nextOffset;
                tileCounts[tileIndex] = tileCommandOffsets[tileIndex];
            }

            var entryCount = tileCommandOffsets[tileCount];
            tileCommandIndices = new int[entryCount];
            for (var commandIndex = 0; commandIndex < commands.Length; commandIndex++)
            {
                var boundsOffset = checked(commandIndex * 4);
                var startX = commandTileBounds[boundsOffset];
                var startY = commandTileBounds[boundsOffset + 1];
                var endX = commandTileBounds[boundsOffset + 2];
                var endY = commandTileBounds[boundsOffset + 3];
                if (startX >= endX || startY >= endY) continue;

                // Commands are emitted in source order, preserving per-tile blending.
                for (var tileY = startY; tileY < endY; tileY++)
                {
                    var tileIndex = tileY * tilesX + startX;
                    for (var tileX = startX; tileX < endX; tileX++, tileIndex++)
                    {
                        var writeIndex = tileCounts[tileIndex]++;
                        if ((uint)writeIndex >= (uint)entryCount)
                        {
                            return false;
                        }

                        tileCommandIndices[writeIndex] = commandIndex;
                    }
                }
            }

            return true;
        }
        catch
        {
            tileCommandIndices = Array.Empty<int>();
            return false;
        }
    }

    private int[] EnsureReference3DCpuRasterTileCommandCounts(int tileCount)
    {
        if (_reference3DCpuRasterTileCommandCounts is null
            || _reference3DCpuRasterTileCommandCounts.Length < tileCount)
        {
            _reference3DCpuRasterTileCommandCounts = new int[tileCount];
        }

        return _reference3DCpuRasterTileCommandCounts;
    }

    private int[] EnsureReference3DCpuRasterTileCommandOffsets(int offsetCount)
    {
        if (_reference3DCpuRasterTileCommandOffsets is null
            || _reference3DCpuRasterTileCommandOffsets.Length < offsetCount)
        {
            _reference3DCpuRasterTileCommandOffsets = new int[offsetCount];
        }

        return _reference3DCpuRasterTileCommandOffsets;
    }

    private int[] EnsureReference3DCpuRasterCommandIndices(int entryCount)
    {
        if (_reference3DCpuRasterCommandIndices is null
            || _reference3DCpuRasterCommandIndices.Length < entryCount)
        {
            _reference3DCpuRasterCommandIndices = new int[entryCount];
        }

        return _reference3DCpuRasterCommandIndices;
    }

    private int[] EnsureReference3DCpuRasterCommandTileBounds(int commandCount)
    {
        var requiredLength = checked(commandCount * 4);
        if (_reference3DCpuRasterCommandTileBounds is null
            || _reference3DCpuRasterCommandTileBounds.Length < requiredLength)
        {
            _reference3DCpuRasterCommandTileBounds = new int[requiredLength];
        }

        return _reference3DCpuRasterCommandTileBounds;
    }

    private static bool TryGetReference3DCpuRasterTileRange(
        Reference3DCpuRasterCommand command,
        int width,
        int height,
        int tilesX,
        int tilesY,
        out int startX,
        out int startY,
        out int endX,
        out int endY,
        out bool intersectsViewport)
    {
        startX = 0;
        startY = 0;
        endX = 0;
        endY = 0;
        intersectsViewport = false;
        if (!float.IsFinite(command.MinX)
            || !float.IsFinite(command.MinY)
            || !float.IsFinite(command.MaxX)
            || !float.IsFinite(command.MaxY)
            || command.MaxX <= command.MinX
            || command.MaxY <= command.MinY)
        {
            return false;
        }

        if (command.MaxX <= 0f
            || command.MinX >= width
            || command.MaxY <= 0f
            || command.MinY >= height)
        {
            return true;
        }

        var clippedMinX = Math.Max(0f, command.MinX);
        var clippedMinY = Math.Max(0f, command.MinY);
        var clippedMaxX = Math.Min(width, command.MaxX);
        var clippedMaxY = Math.Min(height, command.MaxY);
        var startTileX = MathF.Floor(clippedMinX / Reference3DCpuRasterTileSize);
        var startTileY = MathF.Floor(clippedMinY / Reference3DCpuRasterTileSize);
        var endTileX = MathF.Ceiling(clippedMaxX / Reference3DCpuRasterTileSize);
        var endTileY = MathF.Ceiling(clippedMaxY / Reference3DCpuRasterTileSize);
        if (!float.IsFinite(startTileX)
            || !float.IsFinite(startTileY)
            || !float.IsFinite(endTileX)
            || !float.IsFinite(endTileY))
        {
            return false;
        }

        startX = Math.Clamp((int)startTileX, 0, tilesX);
        startY = Math.Clamp((int)startTileY, 0, tilesY);
        endX = Math.Clamp((int)endTileX, 0, tilesX);
        endY = Math.Clamp((int)endTileY, 0, tilesY);
        intersectsViewport = startX < endX && startY < endY;
        return true;
    }

    private void ClearReference3DCpuRasterBuffer()
    {
        ClearReference3DPlaybackRasterWorker();
        _reference3DCpuRasterPixels = null;
        _reference3DCpuRasterTileCommandCounts = null;
        _reference3DCpuRasterTileCommandOffsets = null;
        _reference3DCpuRasterCommandIndices = null;
        _reference3DCpuRasterCommandTileBounds = null;
        ClearReference3DCpuRasterBitmap();
        _reference3DPlaybackGdiRasterSurface?.Dispose();
        _reference3DPlaybackGdiRasterSurface = null;
        _reference3DFastPlaybackBitmapGraphics?.Dispose();
        _reference3DFastPlaybackBitmapGraphics = null;
        _reference3DFastPlaybackBitmap?.Dispose();
        _reference3DFastPlaybackBitmap = null;
        _reference3DFastPlaybackBitmapWidth = 0;
        _reference3DFastPlaybackBitmapHeight = 0;
        _reference3DPlaybackBackgroundBitmapGraphics?.Dispose();
        _reference3DPlaybackBackgroundBitmapGraphics = null;
        _reference3DPlaybackBackgroundBitmap?.Dispose();
        _reference3DPlaybackBackgroundBitmap = null;
        _reference3DPlaybackBackgroundBitmapWidth = 0;
        _reference3DPlaybackBackgroundBitmapHeight = 0;
        _reference3DPlaybackBackgroundPixels = null;
        _reference3DPlaybackBackgroundKey = default;
        _reference3DPlaybackBackgroundReady = false;
    }

    private void ClearReference3DCpuRasterBitmap()
    {
        _reference3DCpuRasterBitmap?.Dispose();
        _reference3DCpuRasterBitmap = null;
        _reference3DCpuRasterBitmapWidth = 0;
        _reference3DCpuRasterBitmapHeight = 0;
        _reference3DCpuRasterBitmapTargetGeneration = 0;
    }

    private void ResetReference3DCpuRasterMetrics()
    {
        _reference3DCpuRasterUsedThisFrame = false;
        LastReference3DCpuRasterWorkers = 0;
        LastReference3DCpuRasterTiles = 0;
        LastReference3DCpuRasterCommands = 0;
        LastReference3DCpuRasterMilliseconds = 0;
        LastReference3DCpuRasterUploadMilliseconds = 0;
        LastReference3DCpuRasterBitmapBuilds = 0;
        LastReference3DCpuRasterBitmapReuses = 0;
        LastReference3DCpuRasterPreparationWorkers = 0;
        LastReference3DCpuRasterPreparationMilliseconds = 0;
        LastReference3DPlaybackBackgroundMilliseconds = 0;
        LastReference3DPlaybackBackgroundReadbackMilliseconds = 0;
        LastReference3DPlaybackBitmapWriteMilliseconds = 0;
        LastReference3DPlaybackBlitMilliseconds = 0;
        LastReference3DCpuRasterScale = 1f;
    }

    private static void RasterReference3DCpuTile(
        int[] pixels,
        int width,
        Reference3DCpuRasterCommand[] commands,
        int[] commandIndices,
        int commandStart,
        int commandEnd,
        int tileLeft,
        int tileTop,
        int tileRight,
        int tileBottom,
        float[] coverage)
    {
        for (var commandListIndex = commandStart;
             commandListIndex < commandEnd;
             commandListIndex++)
        {
            var command = commands[commandIndices[commandListIndex]];
            if (command.MaxX <= tileLeft
                || command.MinX >= tileRight
                || command.MaxY <= tileTop
                || command.MinY >= tileBottom)
            {
                continue;
            }

            if (command.Fill)
            {
                RasterReference3DCpuFill(
                    pixels,
                    width,
                    command,
                    tileLeft,
                    tileTop,
                    tileRight,
                    tileBottom,
                    coverage);
            }
            else
            {
                RasterReference3DCpuStroke(
                    pixels,
                    width,
                    command,
                    tileLeft,
                    tileTop,
                    tileRight,
                    tileBottom,
                    coverage);
            }
        }
    }

    private static void RasterReference3DCpuFill(
        int[] pixels,
        int width,
        Reference3DCpuRasterCommand command,
        int tileLeft,
        int tileTop,
        int tileRight,
        int tileBottom,
        float[] coverage)
    {
        var left = Math.Max(tileLeft, (int)MathF.Floor(command.MinX));
        var right = Math.Min(tileRight, (int)MathF.Ceiling(command.MaxX));
        var top = Math.Max(tileTop, (int)MathF.Floor(command.MinY));
        var bottom = Math.Min(tileBottom, (int)MathF.Ceiling(command.MaxY));
        if (left >= right || top >= bottom) return;

        for (var y = top; y < bottom; y++)
        {
            Array.Clear(coverage, left - tileLeft, right - left);
            var scanline = y - command.FillScanlineStartY;
            if ((uint)scanline < (uint)(command.FillScanlineOffsets.Length - 1))
            {
                var spanStart = command.FillScanlineOffsets[scanline];
                var spanEnd = command.FillScanlineOffsets[scanline + 1];
                for (var spanIndex = spanStart; spanIndex < spanEnd; spanIndex++)
                {
                    AccumulateReference3DCpuSpan(
                        coverage,
                        tileLeft,
                        left,
                        right,
                        command.FillScanlineSpans[spanIndex].Start,
                        command.FillScanlineSpans[spanIndex].End,
                        0.5f);
                }
            }

            for (var x = left; x < right; x++)
            {
                var amount = Math.Clamp(coverage[x - tileLeft], 0f, 1f);
                if (amount <= 0f) continue;
                var pixelIndex = y * width + x;
                var argb = command.SampleArgb(x + 0.5f, y + 0.5f);
                if (command.Opaque && amount >= 0.999999f)
                {
                    pixels[pixelIndex] = argb;
                }
                else
                {
                    BlendReference3DCpuPixel(pixels, pixelIndex, argb, amount);
                }
            }
        }
    }

    private static void AccumulateReference3DCpuSpan(
        float[] coverage,
        int tileLeft,
        int left,
        int right,
        float start,
        float end,
        float sampleWeight = 0.5f)
    {
        if (!float.IsFinite(start) || !float.IsFinite(end) || end <= start) return;
        start = Math.Max(start, left);
        end = Math.Min(end, right);
        if (end <= start) return;
        var first = Math.Max(left, (int)MathF.Floor(start));
        var last = Math.Min(right - 1, (int)MathF.Ceiling(end) - 1);
        for (var x = first; x <= last; x++)
        {
            var overlap = Math.Min(x + 1f, end) - Math.Max(x, start);
            if (overlap > 0f) coverage[x - tileLeft] += overlap * sampleWeight;
        }
    }

    private static void RasterReference3DCpuStroke(
        int[] pixels,
        int width,
        Reference3DCpuRasterCommand command,
        int tileLeft,
        int tileTop,
        int tileRight,
        int tileBottom,
        float[] coverage)
    {
        var left = Math.Max(tileLeft, (int)MathF.Floor(command.MinX));
        var right = Math.Min(tileRight, (int)MathF.Ceiling(command.MaxX));
        var top = Math.Max(tileTop, (int)MathF.Floor(command.MinY));
        var bottom = Math.Min(tileBottom, (int)MathF.Ceiling(command.MaxY));
        if (left >= right || top >= bottom) return;

        var radius = command.StrokeWidth * 0.5f;
        var radiusSquared = radius * radius;
        var segmentCount = command.Segments.Length;
        if (segmentCount == 0) return;
        if (segmentCount <= 128)
        {
            Span<int> candidateIndices = stackalloc int[segmentCount];
            var candidateCount = CollectReference3DCpuStrokeSegments(
                command.Segments,
                tileLeft,
                tileTop,
                tileRight,
                tileBottom,
                radius,
                candidateIndices);
            if (candidateCount == 0) return;
            var candidates = candidateIndices[..candidateCount];
            if (command.RasterScale < 0.999999f)
            {
                RasterReference3DCpuStrokeFast(
                    pixels,
                    width,
                    command,
                    left,
                    top,
                    right,
                    bottom,
                    radius,
                    radiusSquared,
                    candidates,
                    tileLeft,
                    coverage);
            }
            else
            {
                RasterReference3DCpuStrokeWithCandidates(
                    pixels,
                    width,
                    command,
                    left,
                    top,
                    right,
                    bottom,
                    radius,
                    radiusSquared,
                    candidates,
                    tileLeft,
                    coverage);
            }
            return;
        }

        var rentedCandidateIndices = ArrayPool<int>.Shared.Rent(segmentCount);
        try
        {
            var candidateCount = CollectReference3DCpuStrokeSegments(
                command.Segments,
                tileLeft,
                tileTop,
                tileRight,
                tileBottom,
                radius,
                rentedCandidateIndices);
            if (candidateCount == 0) return;
            var candidates = rentedCandidateIndices.AsSpan(0, candidateCount);
            if (command.RasterScale < 0.999999f)
            {
                RasterReference3DCpuStrokeFast(
                    pixels,
                    width,
                    command,
                    left,
                    top,
                    right,
                    bottom,
                    radius,
                    radiusSquared,
                    candidates,
                    tileLeft,
                    coverage);
            }
            else
            {
                RasterReference3DCpuStrokeWithCandidates(
                    pixels,
                    width,
                    command,
                    left,
                    top,
                    right,
                    bottom,
                    radius,
                    radiusSquared,
                    candidates,
                    tileLeft,
                    coverage);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rentedCandidateIndices, clearArray: false);
        }
    }

    private static void RasterReference3DCpuStrokeFast(
        int[] pixels,
        int width,
        Reference3DCpuRasterCommand command,
        int left,
        int top,
        int right,
        int bottom,
        float radius,
        float radiusSquared,
        ReadOnlySpan<int> candidateIndices,
        int tileLeft,
        float[] coverage)
    {
        var innerRadius = Math.Max(0f, radius - 0.5f);
        var innerRadiusSquared = innerRadius * innerRadius;
        var outerRadius = radius + 0.5f;
        var outerRadiusSquared = outerRadius * outerRadius;
        for (var y = top; y < bottom; y++)
        {
            Array.Clear(coverage, left - tileLeft, right - left);
            for (var candidateIndex = 0;
                 candidateIndex < candidateIndices.Length;
                 candidateIndex++)
            {
                ref readonly var segment = ref command.Segments[
                    candidateIndices[candidateIndex]];
                var segmentLeft = Math.Max(
                    left,
                    (int)MathF.Floor(segment.MinX - outerRadius));
                var segmentRight = Math.Min(
                    right,
                    (int)MathF.Ceiling(segment.MaxX + outerRadius));
                if (segmentLeft >= segmentRight
                    || y < segment.MinY - outerRadius
                    || y > segment.MaxY + outerRadius)
                {
                    continue;
                }

                for (var x = segmentLeft; x < segmentRight; x++)
                {
                    var distanceSquared = DistanceSquaredToReference3DCpuSegment(
                        x + 0.5f,
                        y + 0.5f,
                        in segment);
                    if (!float.IsFinite(distanceSquared)
                        || distanceSquared >= outerRadiusSquared)
                    {
                        continue;
                    }

                    var covered = distanceSquared <= innerRadiusSquared
                        ? 1f
                        : Math.Clamp(
                            outerRadius - MathF.Sqrt(Math.Max(0f, distanceSquared)),
                            0f,
                            1f);
                    var coverageIndex = x - tileLeft;
                    if (covered > coverage[coverageIndex])
                    {
                        coverage[coverageIndex] = covered;
                    }
                }
            }

            for (var x = left; x < right; x++)
            {
                var covered = coverage[x - tileLeft];
                if (covered <= 0f) continue;
                var pixelIndex = y * width + x;
                var argb = command.SampleArgb(x + 0.5f, y + 0.5f);
                if (command.Opaque && covered >= 0.999999f)
                {
                    pixels[pixelIndex] = argb;
                }
                else
                {
                    BlendReference3DCpuPixel(pixels, pixelIndex, argb, covered);
                }
            }
        }
    }

    private static int CollectReference3DCpuStrokeSegments(
        Reference3DCpuRasterSegment[] segments,
        int tileLeft,
        int tileTop,
        int tileRight,
        int tileBottom,
        float radius,
        Span<int> destination)
    {
        var expandedLeft = tileLeft - radius;
        var expandedTop = tileTop - radius;
        var expandedRight = tileRight + radius;
        var expandedBottom = tileBottom + radius;
        var count = 0;
        for (var index = 0; index < segments.Length; index++)
        {
            ref readonly var segment = ref segments[index];
            if (segment.MaxX < expandedLeft
                || segment.MinX > expandedRight
                || segment.MaxY < expandedTop
                || segment.MinY > expandedBottom)
            {
                continue;
            }

            destination[count++] = index;
        }

        return count;
    }

    private static void RasterReference3DCpuStrokeWithCandidates(
        int[] pixels,
        int width,
        Reference3DCpuRasterCommand command,
        int left,
        int top,
        int right,
        int bottom,
        float radius,
        float radiusSquared,
        ReadOnlySpan<int> candidateIndices,
        int tileLeft,
        float[] coverage)
    {
        for (var y = top; y < bottom; y++)
        {
            Array.Clear(coverage, left - tileLeft, right - left);
            for (var candidateIndex = 0;
                 candidateIndex < candidateIndices.Length;
                 candidateIndex++)
            {
                ref readonly var segment = ref command.Segments[
                    candidateIndices[candidateIndex]];
                var segmentLeft = Math.Max(
                    left,
                    (int)MathF.Floor(segment.MinX - radius));
                var segmentRight = Math.Min(
                    right,
                    (int)MathF.Ceiling(segment.MaxX + radius));
                if (segmentLeft >= segmentRight
                    || y < segment.MinY - radius
                    || y > segment.MaxY + radius)
                {
                    continue;
                }

                for (var x = segmentLeft; x < segmentRight; x++)
                {
                    var coverageIndex = x - tileLeft;
                    var mask = (int)coverage[coverageIndex];
                    if ((mask & 1) == 0
                        && DistanceSquaredToReference3DCpuSegment(
                            x + 0.25f,
                            y + 0.25f,
                            in segment) <= radiusSquared)
                    {
                        mask |= 1;
                    }
                    if ((mask & 2) == 0
                        && DistanceSquaredToReference3DCpuSegment(
                            x + 0.75f,
                            y + 0.25f,
                            in segment) <= radiusSquared)
                    {
                        mask |= 2;
                    }
                    if ((mask & 4) == 0
                        && DistanceSquaredToReference3DCpuSegment(
                            x + 0.25f,
                            y + 0.75f,
                            in segment) <= radiusSquared)
                    {
                        mask |= 4;
                    }
                    if ((mask & 8) == 0
                        && DistanceSquaredToReference3DCpuSegment(
                            x + 0.75f,
                            y + 0.75f,
                            in segment) <= radiusSquared)
                    {
                        mask |= 8;
                    }
                    coverage[coverageIndex] = mask;
                }
            }

            for (var x = left; x < right; x++)
            {
                var covered = (int)coverage[x - tileLeft];
                if (covered == 0) continue;
                var pixelIndex = y * width + x;
                var argb = command.SampleArgb(x + 0.5f, y + 0.5f);
                if (command.Opaque && covered == 15)
                {
                    pixels[pixelIndex] = argb;
                }
                else
                {
                    BlendReference3DCpuPixel(
                        pixels,
                        pixelIndex,
                        argb,
                        BitOperations.PopCount((uint)covered) * 0.25f);
                }
            }
        }
    }

    private static float DistanceSquaredToReference3DCpuSegment(
        float x,
        float y,
        in Reference3DCpuRasterSegment segment)
    {
        if (segment.InverseLengthSquared <= 0f)
        {
            var degenerateDx = x - segment.A.X;
            var degenerateDy = y - segment.A.Y;
            return degenerateDx * degenerateDx + degenerateDy * degenerateDy;
        }

        var amount = ((x - segment.A.X) * segment.Dx
                + (y - segment.A.Y) * segment.Dy)
            * segment.InverseLengthSquared;
        amount = Math.Clamp(amount, 0f, 1f);
        var projectedX = segment.A.X + segment.Dx * amount;
        var projectedY = segment.A.Y + segment.Dy * amount;
        var distanceDx = x - projectedX;
        var distanceDy = y - projectedY;
        return distanceDx * distanceDx + distanceDy * distanceDy;
    }

    private static float NearestReference3DCpuSegmentDistanceSquared(
        Reference3DCpuRasterSegment[] segments,
        ReadOnlySpan<int> candidateIndices,
        float x,
        float y,
        float radius,
        float nearest)
    {
        for (var index = 0; index < candidateIndices.Length; index++)
        {
            ref readonly var segment = ref segments[candidateIndices[index]];
            if (x < segment.MinX - radius
                || x > segment.MaxX + radius
                || y < segment.MinY - radius
                || y > segment.MaxY + radius)
            {
                continue;
            }

            var distanceSquared = DistanceSquaredToReference3DCpuSegment(x, y, in segment);
            if (distanceSquared < nearest) nearest = distanceSquared;
        }

        return nearest;
    }

    private static void BlendReference3DCpuPixel(
        int[] pixels,
        int index,
        int argb,
        float coverage)
    {
        var coverageByte = (int)Math.Clamp(MathF.Round(coverage * 255f), 0f, 255f);
        if (coverageByte <= 0) return;
        var sourceAlpha = (argb >> 24) & 0xff;
        if (coverageByte == 255 && sourceAlpha == 255)
        {
            pixels[index] = argb;
            return;
        }

        sourceAlpha = (sourceAlpha * coverageByte + 127) / 255;
        if (sourceAlpha <= 0) return;
        var sourceRed = ((argb >> 16) & 0xff) * sourceAlpha;
        var sourceGreen = ((argb >> 8) & 0xff) * sourceAlpha;
        var sourceBlue = (argb & 0xff) * sourceAlpha;
        sourceRed = (sourceRed + 127) / 255;
        sourceGreen = (sourceGreen + 127) / 255;
        sourceBlue = (sourceBlue + 127) / 255;

        var destination = pixels[index];
        var destinationAlpha = (destination >> 24) & 0xff;
        var inverseAlpha = 255 - sourceAlpha;
        var red = sourceRed + (((destination >> 16) & 0xff) * inverseAlpha + 127) / 255;
        var green = sourceGreen + (((destination >> 8) & 0xff) * inverseAlpha + 127) / 255;
        var blue = (sourceBlue & 0xff) + ((destination & 0xff) * inverseAlpha + 127) / 255;
        var alpha = sourceAlpha + (destinationAlpha * inverseAlpha + 127) / 255;
        pixels[index] = Math.Clamp(alpha, 0, 255) << 24
            | Math.Clamp(red, 0, 255) << 16
            | Math.Clamp(green, 0, 255) << 8
            | Math.Clamp(blue, 0, 255);
    }

    private static int SampleGradientArgb(
        Reference3DCpuRasterGradientStop[] stops,
        float position)
    {
        if (stops.Length == 0) return 0;
        if (!float.IsFinite(position)) position = 1f;
        position = Math.Clamp(position, 0f, 1f);
        if (stops.Length == 1) return stops[0].Argb;
        var previous = stops[0];
        if (stops.Length == 2)
        {
            var current = stops[1];
            var length = Math.Max(0.0001f, current.Position - previous.Position);
            var amount = Math.Clamp((position - previous.Position) / length, 0f, 1f);
            return PackReference3DCpuArgb(
                LerpReference3DCpuByte(previous.A, current.A, amount),
                LerpReference3DCpuByte(previous.R, current.R, amount),
                LerpReference3DCpuByte(previous.G, current.G, amount),
                LerpReference3DCpuByte(previous.B, current.B, amount));
        }
        for (var index = 1; index < stops.Length; index++)
        {
            var current = stops[index];
            if (position > current.Position)
            {
                previous = current;
                continue;
            }

            var length = Math.Max(0.0001f, current.Position - previous.Position);
            var amount = Math.Clamp((position - previous.Position) / length, 0f, 1f);
            return PackReference3DCpuArgb(
                LerpReference3DCpuByte(previous.A, current.A, amount),
                LerpReference3DCpuByte(previous.R, current.R, amount),
                LerpReference3DCpuByte(previous.G, current.G, amount),
                LerpReference3DCpuByte(previous.B, current.B, amount));
        }
        return stops[^1].Argb;
    }

    private static int LerpReference3DCpuByte(byte first, byte second, float amount) =>
        (int)Math.Clamp(MathF.Round(first + (second - first) * amount), 0f, 255f);

    private static int PackReference3DCpuArgb(int a, int r, int g, int b) =>
        Math.Clamp(a, 0, 255) << 24
        | Math.Clamp(r, 0, 255) << 16
        | Math.Clamp(g, 0, 255) << 8
        | Math.Clamp(b, 0, 255);

    private static int PremultiplyReference3DCpuArgb(int argb)
    {
        var alpha = (argb >> 24) & 0xff;
        var red = ((argb >> 16) & 0xff) * alpha;
        var green = ((argb >> 8) & 0xff) * alpha;
        var blue = (argb & 0xff) * alpha;
        return alpha << 24
            | ((blue + 127) / 255) << 16
            | ((green + 127) / 255) << 8
            | ((red + 127) / 255);
    }

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}
