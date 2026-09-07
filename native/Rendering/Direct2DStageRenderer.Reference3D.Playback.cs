using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class Direct2DStageRenderer
{
    private const uint Reference3DSourceCopyRasterOperation = 0x00CC0020;

    private readonly record struct Reference3DPlaybackViewKey(
        int Width,
        int Height,
        SceneDimension Dimension,
        CameraProjection Projection,
        float ProjectionBlend,
        float Yaw,
        float Pitch,
        float Distance,
        float ZoomScale,
        float TargetX,
        float TargetY,
        float TargetZ);

    private readonly record struct Reference3DPlaybackRasterRequestKey(
        long SessionId,
        VectorScene Scene,
        int Frame,
        int Width,
        int Height,
        Reference3DRenderItem[] RenderItems,
        int[] BackgroundPixels,
        Reference3DPlaybackBackgroundKey BackgroundKey,
        Reference3DPlaybackViewKey View);

    private sealed record Reference3DPlaybackRasterRequest(
        Reference3DPlaybackRasterRequestKey Key,
        Reference3DCpuRasterCommand[] Commands,
        Reference3DCpuRasterTileBins? TileBins,
        int TargetWidth,
        int TargetHeight,
        float RasterScale,
        int DrawnObjects,
        long Sequence,
        bool AllowGdiRaster = true,
        Bitmap? BackgroundBitmap = null);

    // Each playback producer owns one surface for its whole lifetime. Published
    // leases keep their slot busy until the UI replaces the presented bitmap,
    // so a producer never writes into a surface that is currently on screen.
    private sealed class Reference3DPlaybackGdiRasterSurface : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<Slot> _slots = [];
        private bool _disposed;

        public Reference3DPlaybackGdiBrushCache BrushCache { get; } = new();

        public Lease Acquire(int width, int height)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(
                        nameof(Reference3DPlaybackGdiRasterSurface));
                }

                var slot = _slots.FirstOrDefault(candidate => !candidate.InUse);
                if (slot is null)
                {
                    slot = new Slot();
                    _slots.Add(slot);
                }

                slot.InUse = true;
                try
                {
                    slot.Ensure(width, height);
                    return new Lease(this, slot);
                }
                catch
                {
                    slot.InUse = false;
                    throw;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var slot in _slots)
                {
                    if (!slot.InUse) slot.Dispose();
                }
            }
            BrushCache.Dispose();
        }

        private void Release(Slot slot)
        {
            lock (_gate)
            {
                slot.InUse = false;
                if (_disposed) slot.Dispose();
            }
        }

        internal sealed class Slot : IDisposable
        {
            private Bitmap? _bitmap;
            private Graphics? _graphics;

            public bool InUse { get; set; }

            public Bitmap Bitmap => _bitmap
                ?? throw new InvalidOperationException(
                    "The GDI playback surface slot is not initialized.");

            public Graphics Graphics => _graphics
                ?? throw new InvalidOperationException(
                    "The GDI playback surface slot is not initialized.");

                public void Ensure(int width, int height)
                {
                    if (_bitmap is not null
                    && _bitmap.Width == width
                    && _bitmap.Height == height)
                    {
                        _graphics ??= Graphics.FromImage(_bitmap);
                        return;
                    }

                _graphics?.Dispose();
                _graphics = null;
                _bitmap?.Dispose();
                _bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                _graphics = Graphics.FromImage(_bitmap);
            }

            public void ReleaseRenderGraphics()
            {
                _graphics?.Dispose();
                _graphics = null;
            }

            public void Dispose()
            {
                _graphics?.Dispose();
                _graphics = null;
                _bitmap?.Dispose();
                _bitmap = null;
                InUse = false;
            }
        }

        internal sealed class Lease : IDisposable
        {
            private Reference3DPlaybackGdiRasterSurface? _surface;
            private Slot? _slot;
            private Bitmap? _ownedBitmap;
            private Graphics? _ownedGraphics;
            private Graphics? _presentationGraphics;
            private int _disposed;

            internal Lease(
                Reference3DPlaybackGdiRasterSurface surface,
                Slot slot)
            {
                _surface = surface;
                _slot = slot;
            }

            private Lease(Bitmap bitmap, Graphics graphics)
            {
                _ownedBitmap = bitmap;
                _ownedGraphics = graphics;
            }

            public Bitmap Bitmap => _slot?.Bitmap
                ?? _ownedBitmap
                ?? throw new ObjectDisposedException(nameof(Lease));

            public Graphics Graphics => _slot?.Graphics
                ?? _ownedGraphics
                ?? throw new ObjectDisposedException(nameof(Lease));

            public Graphics PresentationGraphics => _presentationGraphics
                ??= Graphics.FromImage(Bitmap);

            public void PrepareForPresentation()
            {
                _slot?.ReleaseRenderGraphics();
            }

            public static Lease Own(Bitmap bitmap, Graphics graphics) =>
                new(bitmap, graphics);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

                var presentationGraphics = _presentationGraphics;
                _presentationGraphics = null;
                presentationGraphics?.Dispose();
                var slot = _slot;
                _slot = null;
                var surface = _surface;
                _surface = null;
                if (slot is not null && surface is not null)
                {
                    surface.Release(slot);
                    return;
                }

                var graphics = _ownedGraphics;
                _ownedGraphics = null;
                graphics?.Dispose();
                var bitmap = _ownedBitmap;
                _ownedBitmap = null;
                bitmap?.Dispose();
            }
        }
    }

    private sealed class Reference3DPlaybackGdiBrushCache : IDisposable
    {
        private readonly Dictionary<int, SolidBrush> _solidBrushes = [];
        private readonly Dictionary<
            Reference3DCpuRasterGradientStop[],
            LinearGradientBrush> _linearBrushes = new(
                Reference3DCpuRasterGradientStopComparer.Instance);

        public Brush GetBrush(Reference3DCpuRasterCommand command)
        {
            if (!command.HasGradient || command.GradientStops.Length == 0)
            {
                return GetSolidBrush(command.SolidArgb);
            }

            var stops = command.GradientStops;
            // GDI+ requires at least two strictly ordered ColorBlend entries.
            // Imported or optically-lit materials can legitimately collapse to
            // one stop; keep that command on the solid fast path instead of
            // failing the complete playback raster and invoking the CPU fallback.
            if (stops.Length < 2)
            {
                return GetSolidBrush(stops[^1].Argb);
            }

            var axisX = command.GradientEndScreen.X - command.GradientStartScreen.X;
            var axisY = command.GradientEndScreen.Y - command.GradientStartScreen.Y;
            var axisLengthSquared = axisX * axisX + axisY * axisY;
            if (!float.IsFinite(axisLengthSquared) || axisLengthSquared <= 0.000001f)
            {
                return GetSolidBrush(stops[^1].Argb);
            }

            if (!_linearBrushes.TryGetValue(stops, out var brush))
            {
                var colors = new Color[stops.Length];
                var positions = new float[stops.Length];
                var firstPosition = stops[0].Position;
                var lastPosition = stops[^1].Position;
                var positionLength = lastPosition - firstPosition;
                if (!float.IsFinite(firstPosition)
                    || !float.IsFinite(lastPosition)
                    || !float.IsFinite(positionLength)
                    || positionLength <= 0.000001f)
                {
                    return GetSolidBrush(stops[^1].Argb);
                }

                var previousPosition = firstPosition;
                for (var index = 0; index < stops.Length; index++)
                {
                    if (!float.IsFinite(stops[index].Position)
                        || index > 0 && stops[index].Position <= previousPosition)
                    {
                        return GetSolidBrush(stops[^1].Argb);
                    }

                    colors[index] = Color.FromArgb(stops[index].Argb);
                    positions[index] = Math.Clamp(
                        (stops[index].Position - firstPosition) / positionLength,
                        0f,
                        1f);
                    previousPosition = stops[index].Position;
                }
                positions[0] = 0f;
                positions[^1] = 1f;
                try
                {
                    brush = new LinearGradientBrush(
                        new PointF(0f, 0f),
                        new PointF(1f, 0f),
                        colors[0],
                        colors[^1])
                    {
                        WrapMode = WrapMode.Clamp
                    };
                    brush.InterpolationColors = new ColorBlend
                    {
                        Colors = colors,
                        Positions = positions
                    };
                }
                catch (ArgumentException)
                {
                    brush?.Dispose();
                    return GetSolidBrush(stops[^1].Argb);
                }
                _linearBrushes.Add(stops, brush);
            }

            try
            {
                // Map the unit gradient axis to the command's screen-space
                // axis. Use a perpendicular second basis vector so vertical
                // gradients remain invertible instead of producing a singular
                // GDI+ matrix. The matrix is short-lived; the brush retains
                // its own native copy.
                using var transform = new Matrix(
                    axisX,
                    axisY,
                    -axisY,
                    axisX,
                    command.GradientStartScreen.X,
                    command.GradientStartScreen.Y);
                brush.Transform = transform;
            }
            catch (ArgumentException)
            {
                return GetSolidBrush(stops[^1].Argb);
            }
            return brush;
        }

        public void Dispose()
        {
            foreach (var brush in _solidBrushes.Values) brush.Dispose();
            foreach (var brush in _linearBrushes.Values) brush.Dispose();
            _solidBrushes.Clear();
            _linearBrushes.Clear();
        }

        private SolidBrush GetSolidBrush(int argb)
        {
            if (_solidBrushes.TryGetValue(argb, out var brush)) return brush;
            brush = new SolidBrush(Color.FromArgb(argb));
            _solidBrushes.Add(argb, brush);
            return brush;
        }
    }

    private sealed class Reference3DCpuRasterGradientStopComparer :
        IEqualityComparer<Reference3DCpuRasterGradientStop[]>
    {
        public static Reference3DCpuRasterGradientStopComparer Instance { get; } = new();

        public bool Equals(
            Reference3DCpuRasterGradientStop[]? left,
            Reference3DCpuRasterGradientStop[]? right) =>
            ReferenceEquals(left, right)
            || left is not null
                && right is not null
                && left.AsSpan().SequenceEqual(right);

        public int GetHashCode(Reference3DCpuRasterGradientStop[] stops)
        {
            var hash = new HashCode();
            foreach (var stop in stops) hash.Add(stop);
            return hash.ToHashCode();
        }
    }

    private sealed class Reference3DPlaybackPreparedCommands(
        VectorScene scene,
        int frame,
        int width,
        int height,
        Reference3DPlaybackViewKey view,
        Reference3DRenderItem[] renderItems,
        Reference3DCpuRasterCommand[] commands,
        Reference3DCpuRasterTileBins? tileBins,
        float rasterScale,
        int drawnObjects)
    {
        public VectorScene Scene { get; } = scene;
        public int Frame { get; } = frame;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public Reference3DPlaybackViewKey View { get; } = view;
        public Reference3DRenderItem[] RenderItems { get; } = renderItems;
        public Reference3DCpuRasterCommand[] Commands { get; } = commands;
        public Reference3DCpuRasterTileBins? TileBins { get; } = tileBins;
        public float RasterScale { get; } = rasterScale;
        public int DrawnObjects { get; } = drawnObjects;

        public bool Matches(StageControl stage, Reference3DPlaybackViewKey currentView) =>
            ReferenceEquals(Scene, stage.Scene)
            && Frame == stage.Frame
            && Width == stage.Width
            && Height == stage.Height
            && View == currentView;
    }

    private sealed class Reference3DPlaybackRasterResult
    {
        public Reference3DPlaybackRasterResult(
            Reference3DPlaybackRasterRequestKey key,
            int[]? pixels,
            Bitmap? bitmap,
            Reference3DPlaybackGdiRasterSurface.Lease? lease,
            int pixelCount,
            int width,
            int height,
            float rasterScale,
            int drawnObjects,
            int workers,
            int tiles,
            int commands,
            double rasterMilliseconds,
            long sequence)
        {
            var ownerCount = (pixels is null ? 0 : 1)
                + (bitmap is null ? 0 : 1)
                + (lease is null ? 0 : 1);
            if (ownerCount != 1)
            {
                throw new ArgumentException(
                    "A playback raster result must own exactly one raster payload.");
            }
            Key = key;
            _pixels = pixels;
            _bitmap = bitmap;
            _lease = lease;
            PixelCount = pixelCount;
            Width = width;
            Height = height;
            RasterScale = rasterScale;
            DrawnObjects = drawnObjects;
            Workers = workers;
            Tiles = tiles;
            Commands = commands;
            RasterMilliseconds = rasterMilliseconds;
            Sequence = sequence;
        }

        public Reference3DPlaybackRasterRequestKey Key { get; }

        private int[]? _pixels;

        private Bitmap? _bitmap;

        private Reference3DPlaybackGdiRasterSurface.Lease? _lease;

        private int _disposed;

        public int PixelCount { get; }

        public int Width { get; }

        public int Height { get; }

        public float RasterScale { get; }

        public int DrawnObjects { get; }

        public int Workers { get; }

        public int Tiles { get; }

        public int Commands { get; }

        public double RasterMilliseconds { get; }

        public long Sequence { get; }

        public bool TryTakePixels(out int[] pixels)
        {
            pixels = Interlocked.Exchange(ref _pixels, null)!;
            if (pixels is null) return false;
            if (Volatile.Read(ref _disposed) == 0) return true;

            ArrayPool<int>.Shared.Return(pixels, clearArray: false);
            pixels = null!;
            return false;
        }

        public bool TryTakeBitmap(out Bitmap bitmap)
        {
            bitmap = Interlocked.Exchange(ref _bitmap, null)!;
            if (bitmap is null) return false;
            if (Volatile.Read(ref _disposed) == 0) return true;

            bitmap.Dispose();
            bitmap = null!;
            return false;
        }

        public bool TryTakeLease(
            out Reference3DPlaybackGdiRasterSurface.Lease lease)
        {
            lease = Interlocked.Exchange(ref _lease, null)!;
            if (lease is null) return false;
            if (Volatile.Read(ref _disposed) == 0) return true;

            lease.Dispose();
            lease = null!;
            return false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var pixels = Interlocked.Exchange(ref _pixels, null);
            if (pixels is not null) ArrayPool<int>.Shared.Return(pixels, clearArray: false);
            Interlocked.Exchange(ref _bitmap, null)?.Dispose();
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }
    }

    // A completed playback frame owns its pooled pixels until either the Stage
    // consumes them or the preloader drops the frame. The frame metadata stays
    // available after consumption so duplicate paints can reuse the bitmap.
    private sealed class Reference3DPlaybackRasterFrame : IDisposable
    {
        private int[]? _pixels;
        private Bitmap? _bitmap;
        private Reference3DPlaybackGdiRasterSurface.Lease? _lease;
        private int _disposed;

        public Reference3DPlaybackRasterFrame(
            Reference3DPlaybackRasterRequestKey key,
            int[]? pixels,
            Bitmap? bitmap,
            Reference3DPlaybackGdiRasterSurface.Lease? lease,
            int pixelCount,
            int width,
            int height,
            float rasterScale,
            int drawnObjects,
            int workers,
            int tiles,
            int commands,
            double rasterMilliseconds,
            RenderStats stats)
        {
            var ownerCount = (pixels is null ? 0 : 1)
                + (bitmap is null ? 0 : 1)
                + (lease is null ? 0 : 1);
            if (ownerCount != 1)
            {
                throw new ArgumentException(
                    "A playback raster frame must own exactly one raster payload.");
            }
            Key = key;
            _pixels = pixels;
            _bitmap = bitmap;
            _lease = lease;
            PixelCount = pixelCount;
            Width = width;
            Height = height;
            RasterScale = rasterScale;
            DrawnObjects = drawnObjects;
            Workers = workers;
            Tiles = tiles;
            Commands = commands;
            RasterMilliseconds = rasterMilliseconds;
            Stats = stats;
        }

        public Reference3DPlaybackRasterRequestKey Key { get; }

        public int PixelCount { get; }

        public int Width { get; }

        public int Height { get; }

        public float RasterScale { get; }

        public int DrawnObjects { get; }

        public int Workers { get; }

        public int Tiles { get; }

        public int Commands { get; }

        public double RasterMilliseconds { get; }

        public RenderStats Stats { get; }

        public string MatchFailureReason { get; private set; } = "not_checked";

        public bool Matches(StageControl stage)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                MatchFailureReason = "disposed";
                return false;
            }
            if (!ReferenceEquals(Key.Scene, stage.Scene))
            {
                MatchFailureReason = "scene";
                return false;
            }
            if (Key.Frame != stage.Frame)
            {
                MatchFailureReason = "frame";
                return false;
            }
            if (Key.Width != stage.Width || Key.Height != stage.Height)
            {
                MatchFailureReason = "size";
                return false;
            }
            if (Key.View != CreateReference3DPlaybackViewKey(stage))
            {
                MatchFailureReason = "view";
                return false;
            }
            if (Key.BackgroundKey != CreateReference3DPlaybackBackgroundKey(stage))
            {
                MatchFailureReason = "background";
                return false;
            }

            MatchFailureReason = "matched";
            return true;
        }

        public bool TryTakePixels(out int[] pixels)
        {
            pixels = Interlocked.Exchange(ref _pixels, null)!;
            if (pixels is null) return false;
            if (Volatile.Read(ref _disposed) == 0) return true;

            ArrayPool<int>.Shared.Return(pixels, clearArray: false);
            pixels = null!;
            return false;
        }

        public bool TryTakeBitmap(out Bitmap bitmap)
        {
            bitmap = Interlocked.Exchange(ref _bitmap, null)!;
            if (bitmap is null) return false;
            if (Volatile.Read(ref _disposed) == 0) return true;

            bitmap.Dispose();
            bitmap = null!;
            return false;
        }

        public bool TryTakeLease(
            out Reference3DPlaybackGdiRasterSurface.Lease lease)
        {
            lease = Interlocked.Exchange(ref _lease, null)!;
            if (lease is null) return false;
            if (Volatile.Read(ref _disposed) == 0) return true;

            lease.Dispose();
            lease = null!;
            return false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var pixels = Interlocked.Exchange(ref _pixels, null);
            if (pixels is not null) ArrayPool<int>.Shared.Return(pixels, clearArray: false);
            Interlocked.Exchange(ref _bitmap, null)?.Dispose();
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }
    }

    private sealed class Reference3DPlaybackRasterWorker : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly Action _requestPresent;
        // Keep the GDI surface and its brushes alive across frames. A result
        // holds a lease until the UI has finished presenting that bitmap.
        private readonly Reference3DPlaybackGdiRasterSurface _gdiSurface = new();
        private Reference3DPlaybackRasterRequest? _pending;
        private Reference3DPlaybackRasterResult? _ready;
        private Thread? _worker;
        private string _failureReason = string.Empty;
        private bool _failed;
        private bool _disposed;
        private int _synchronizationDisposed;

        public Reference3DPlaybackRasterWorker(Action requestPresent)
        {
            _requestPresent = requestPresent ?? throw new ArgumentNullException(nameof(requestPresent));
        }

        public bool Failed
        {
            get
            {
                lock (_gate) return _failed;
            }
        }

        public string FailureReason
        {
            get
            {
                lock (_gate) return _failureReason;
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed || _worker is not null) return;
                _worker = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "Reference 3D playback raster",
                    Priority = ThreadPriority.BelowNormal
                };
                _worker.Start();
            }
            _wake.Set();
        }

        public void Enqueue(Reference3DPlaybackRasterRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            lock (_gate)
            {
                if (_disposed || _failed) return;
                // Playback is real-time: the newest request replaces work that
                // has not started yet so a slow frame cannot build a backlog.
                _pending = request;
            }
            _wake.Set();
        }

        public bool TryTakeLatest(
            Reference3DPlaybackRasterRequestKey key,
            long currentSequence,
            long lastPresentedSequence,
            out Reference3DPlaybackRasterResult result)
        {
            result = null!;
            lock (_gate)
            {
                if (_ready is null) return false;
                var ready = _ready;
                _ready = null;
                if (ready.Key != key
                    || ready.Sequence > currentSequence
                    || ready.Sequence <= lastPresentedSequence)
                {
                    ready.Dispose();
                    return false;
                }

                result = ready;
                return true;
            }
        }

        public void Dispose()
        {
            Thread? worker;
            Reference3DPlaybackRasterResult? ready;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _pending = null;
                ready = _ready;
                _ready = null;
                worker = _worker;
            }

            ready?.Dispose();
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            try
            {
                _wake.Set();
            }
            catch (ObjectDisposedException)
            {
            }
            var completed = worker is null;
            if (worker is not null)
            {
                try
                {
                    completed = worker.Join(TimeSpan.FromMilliseconds(500));
                }
                catch (ThreadStateException)
                {
                    completed = true;
                }
            }

            if (completed)
            {
                _gdiSurface.Dispose();
                DisposeWorkerSynchronization();
            }
        }

        private void DisposeWorkerSynchronization()
        {
            if (Interlocked.Exchange(ref _synchronizationDisposed, 1) != 0) return;
            _wake.Dispose();
            _cancellation.Dispose();
        }

        private void DropReadyResult()
        {
            Reference3DPlaybackRasterResult? ready;
            lock (_gate)
            {
                ready = _ready;
                _ready = null;
                _pending = null;
            }
            ready?.Dispose();
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    Reference3DPlaybackRasterRequest? request;
                    lock (_gate)
                    {
                        if (_disposed || _cancellation.IsCancellationRequested) return;
                        request = _pending;
                        _pending = null;
                    }

                    if (request is null)
                    {
                        _wake.WaitOne(20);
                        continue;
                    }

                    var result = RasterizeReference3DPlayback(
                        request,
                        _cancellation.Token,
                        gdiSurface: _gdiSurface);
                    if (result is null) continue;

                    var accepted = false;
                    lock (_gate)
                    {
                        if (!_disposed && !_cancellation.IsCancellationRequested)
                        {
                            _ready?.Dispose();
                            _ready = result;
                            accepted = true;
                        }
                    }

                    if (accepted)
                    {
                        try
                        {
                            _requestPresent();
                        }
                        catch
                        {
                            // The Stage may lose its HWND while the worker is
                            // completing. The next UI lifecycle event stops it.
                        }
                    }
                    else
                    {
                        result.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _failed = true;
                        _failureReason = exception.GetType().Name;
                    }
                }
                DropReadyResult();
                AppLog.Warn($"Reference 3D playback raster worker stopped: {exception.Message}");
            }
            finally
            {
                DropReadyResult();
                _gdiSurface.Dispose();
                DisposeWorkerSynchronization();
            }
        }
    }

    private Reference3DPlaybackRasterWorker? _reference3DPlaybackRasterWorker;
    private Reference3DPlaybackRasterFrame? _reference3DPlaybackRasterFrame;
    private Reference3DPlaybackGdiRasterSurface.Lease?
        _reference3DPlaybackPresentedBitmapLease;
    private long _reference3DPlaybackSessionId;
    private Reference3DPlaybackRasterRequestKey _reference3DPlaybackRasterRequestKey;
    private bool _reference3DPlaybackRasterRequestKeyValid;
    private long _reference3DPlaybackRasterRequestSequence;
    private long _reference3DPlaybackRasterPresentedSequence;
    private long _reference3DPlaybackRasterFrameMatchCount;
    private long _reference3DPlaybackRasterFrameMismatchCount;
    private string _reference3DPlaybackRasterFrameLastMismatch = "none";
    private long _reference3DPlaybackRasterFrameSetCount;
    private string _reference3DPlaybackRasterFrameLastSetType = "none";
    private bool _reference3DPlaybackRasterFrameLastSetCast;
    private bool _reference3DPlaybackRasterHasPresentedFrame;
    private RenderStats _reference3DPlaybackRasterLastStats;

    internal double LastReference3DPlaybackRasterMilliseconds { get; private set; }

    internal int LastReference3DPlaybackPresentedFrame { get; private set; } = -1;

    internal string LastReference3DPlaybackRasterFrameMatchFailure =>
        _reference3DPlaybackRasterFrame?.MatchFailureReason ?? "no_frame";

    internal long Reference3DPlaybackRasterFrameMatchCount =>
        Interlocked.Read(ref _reference3DPlaybackRasterFrameMatchCount);

    internal long Reference3DPlaybackRasterFrameMismatchCount =>
        Interlocked.Read(ref _reference3DPlaybackRasterFrameMismatchCount);

    internal string Reference3DPlaybackRasterFrameLastMismatch =>
        Volatile.Read(ref _reference3DPlaybackRasterFrameLastMismatch);

    internal long Reference3DPlaybackRasterFrameSetCount =>
        Interlocked.Read(ref _reference3DPlaybackRasterFrameSetCount);

    internal string Reference3DPlaybackRasterFrameLastSetType =>
        Volatile.Read(ref _reference3DPlaybackRasterFrameLastSetType);

    internal bool Reference3DPlaybackRasterFrameLastSetCast =>
        _reference3DPlaybackRasterFrameLastSetCast;

    internal void SetReference3DPlaybackRasterActive(StageControl stage, bool active)
    {
        if (!active)
        {
            ClearReference3DPlaybackRasterWorker();
            return;
        }

        ClearReference3DPlaybackRasterWorker();
        unchecked
        {
            _reference3DPlaybackSessionId++;
        }
        var worker = new Reference3DPlaybackRasterWorker(
            stage.RequestReference3DPlaybackPresent);
        _reference3DPlaybackRasterWorker = worker;
        _reference3DPlaybackRasterRequestKeyValid = false;
        _reference3DPlaybackRasterRequestSequence = 0;
        _reference3DPlaybackRasterPresentedSequence = 0;
        _reference3DPlaybackRasterFrameMatchCount = 0;
        _reference3DPlaybackRasterFrameMismatchCount = 0;
        _reference3DPlaybackRasterFrameLastMismatch = "none";
        _reference3DPlaybackRasterFrameSetCount = 0;
        _reference3DPlaybackRasterFrameLastSetType = "none";
        _reference3DPlaybackRasterFrameLastSetCast = false;
        _reference3DPlaybackRasterHasPresentedFrame = false;
        _reference3DPlaybackRasterLastStats = default;
        Volatile.Write(ref _lastReference3DPlaybackGdiRasterFailure, "none");
        LastReference3DPlaybackPresentedFrame = -1;
        worker.Start();
    }

    internal void SetReference3DPlaybackRasterFrame(object? frame)
    {
        var next = frame as Reference3DPlaybackRasterFrame;
        Interlocked.Increment(ref _reference3DPlaybackRasterFrameSetCount);
        Volatile.Write(
            ref _reference3DPlaybackRasterFrameLastSetType,
            frame?.GetType().Name ?? "null");
        _reference3DPlaybackRasterFrameLastSetCast = next is not null;
        if (ReferenceEquals(_reference3DPlaybackRasterFrame, next)) return;

        _reference3DPlaybackRasterFrame?.Dispose();
        _reference3DPlaybackRasterFrame = next;
        if (next is null)
        {
            ClearReference3DPlaybackPresentedBitmap();
            _reference3DPlaybackRasterHasPresentedFrame = false;
            _reference3DPlaybackRasterLastStats = default;
            LastReference3DPlaybackPresentedFrame = -1;
        }
        else if (!_reference3DPlaybackRasterHasPresentedFrame)
        {
            _reference3DPlaybackRasterLastStats = default;
            LastReference3DPlaybackPresentedFrame = -1;
        }
    }

    private void InvalidateReference3DPlaybackRasterAfterPresentFailure(
        Reference3DPlaybackRasterFrame? frame = null)
    {
        if (frame is not null && ReferenceEquals(_reference3DPlaybackRasterFrame, frame))
        {
            _reference3DPlaybackRasterFrame = null;
            frame.Dispose();
        }

        _reference3DPlaybackRasterRequestKeyValid = false;
        _reference3DPlaybackRasterHasPresentedFrame = false;
        _reference3DPlaybackRasterLastStats = default;
        LastReference3DPlaybackPresentedFrame = -1;
        ClearReference3DPlaybackPresentedBitmap();
    }

    private bool TryQueueReference3DPlaybackRaster(
        StageControl stage,
        out Reference3DPlaybackRasterRequestKey key)
    {
        key = default;
        var worker = _reference3DPlaybackRasterWorker;
        if (_reference3DPlaybackRasterFrame?.Matches(stage) == true) return true;
        if (worker is null)
        {
            SetReference3DPlaybackRasterActive(stage, active: true);
            worker = _reference3DPlaybackRasterWorker;
        }

        if (worker is null || worker.Failed)
        {
            LastReference3DCpuRasterFallbackReason = worker?.FailureReason
                is { Length: > 0 } reason
                    ? $"async_worker_{reason}"
                    : "async_worker_unavailable";
            return false;
        }

        var backgroundPixels = _reference3DPlaybackBackgroundPixels;
        if (backgroundPixels is null)
        {
            LastReference3DCpuRasterFallbackReason = "background_pixels_unavailable";
            return false;
        }

        var view = CreateReference3DPlaybackViewKey(stage);
        var prepared = stage.Reference3DPlaybackRasterPreparation
            as Reference3DPlaybackPreparedCommands;
        var usePrepared = prepared is not null && prepared.Matches(stage, view);
        var renderItems = usePrepared
            ? prepared!.RenderItems
            : stage.GetReference3DSceneRenderItems();

        key = new Reference3DPlaybackRasterRequestKey(
            _reference3DPlaybackSessionId,
            stage.Scene,
            stage.Frame,
            stage.Width,
            stage.Height,
            renderItems,
            backgroundPixels,
            CreateReference3DPlaybackBackgroundKey(stage),
            view);
        if (_reference3DPlaybackRasterRequestKeyValid
            && _reference3DPlaybackRasterRequestKey == key)
        {
            return true;
        }

        Reference3DCpuRasterCommand[] commands;
        float rasterScale;
        int drawnObjects;
        if (usePrepared)
        {
            commands = prepared!.Commands;
            rasterScale = prepared.RasterScale;
            drawnObjects = prepared.DrawnObjects;
        }
        else if (!TryPrepareReference3DCpuRasterCommands(
                     stage,
                     renderItems,
                     rasterScaleOverride: GetReference3DPlaybackRasterScale(renderItems.Length),
                     maxWorkers: 2,
                     buildFillScanlines: false,
                     buildGradientLookup: false,
                     out commands,
                     out rasterScale,
                     out drawnObjects))
        {
            return false;
        }

        worker.Enqueue(new Reference3DPlaybackRasterRequest(
            key,
            commands,
            usePrepared ? prepared!.TileBins : null,
            stage.Width,
            stage.Height,
            rasterScale,
            drawnObjects,
            ++_reference3DPlaybackRasterRequestSequence));
        _reference3DPlaybackRasterRequestKey = key;
        _reference3DPlaybackRasterRequestKeyValid = true;
        LastReference3DCpuRasterFallbackReason = "queued";
        return true;
    }

    internal object? PrepareReference3DPlaybackRaster(StageControl stage)
    {
        try
        {
            // The preparation stage is reused for every prefetched frame. Its
            // CPU-raster telemetry is otherwise cumulative, which would make
            // the preloader add the same historical time once per frame.
            ResetReference3DCpuRasterMetrics();
            var renderItems = stage.GetReference3DSceneRenderItems();
            if (!TryPrepareReference3DCpuRasterCommands(
                    stage,
                    renderItems,
                    rasterScaleOverride: GetReference3DPlaybackRasterScale(renderItems.Length),
                    maxWorkers: 2,
                    buildFillScanlines: false,
                    buildGradientLookup: false,
                    out var commands,
                    out var rasterScale,
                    out var drawnObjects))
            {
                return null;
            }

            return new Reference3DPlaybackPreparedCommands(
                stage.Scene,
                stage.Frame,
                stage.Width,
                stage.Height,
                CreateReference3DPlaybackViewKey(stage),
                renderItems,
                commands,
                TryBuildReference3DCpuRasterTileBins(
                    commands,
                    stage.Width,
                    stage.Height,
                    rasterScale),
                rasterScale,
                drawnObjects);
        }
        catch
        {
            return null;
        }
    }

    internal object? PrepareReference3DPlaybackRasterFrame(
        StageControl stage,
        object? preparedRaster)
    {
        if (preparedRaster is not Reference3DPlaybackPreparedCommands prepared
            || !prepared.Matches(stage, CreateReference3DPlaybackViewKey(stage)))
        {
            return null;
        }

        try
        {
            EnsureReference3DPlaybackBackground(stage);
            var backgroundPixels = _reference3DPlaybackBackgroundPixels;
            if (backgroundPixels is null) return null;

            var key = new Reference3DPlaybackRasterRequestKey(
                0,
                stage.Scene,
                stage.Frame,
                stage.Width,
                stage.Height,
                prepared.RenderItems,
                backgroundPixels,
                CreateReference3DPlaybackBackgroundKey(stage),
                prepared.View);
            var request = new Reference3DPlaybackRasterRequest(
                key,
                prepared.Commands,
                prepared.TileBins,
                stage.Width,
                stage.Height,
                prepared.RasterScale,
                prepared.DrawnObjects,
                0,
                AllowGdiRaster: true,
                BackgroundBitmap: _reference3DPlaybackBackgroundBitmap);
            var result = RasterizeReference3DPlayback(
                request,
                CancellationToken.None,
                _reference3DPlaybackGdiRasterSurface ??=
                    new Reference3DPlaybackGdiRasterSurface());
            if (result is null) return null;
            LastReference3DPlaybackRasterMilliseconds = result.RasterMilliseconds;

            int[]? pixels = null;
            Bitmap? bitmap = null;
            Reference3DPlaybackGdiRasterSurface.Lease? lease = null;
            try
            {
                if (!result.TryTakePixels(out pixels)
                    && !result.TryTakeLease(out lease)
                    && !result.TryTakeBitmap(out bitmap))
                {
                    return null;
                }

                return new Reference3DPlaybackRasterFrame(
                    result.Key,
                    pixels,
                    bitmap,
                    lease,
                    result.PixelCount,
                    result.Width,
                    result.Height,
                    result.RasterScale,
                    result.DrawnObjects,
                    result.Workers,
                    result.Tiles,
                    result.Commands,
                    result.RasterMilliseconds,
                    CreateReference3DPlaybackRasterStats(
                        stage,
                        result.DrawnObjects));
            }
            catch
            {
                if (pixels is not null)
                {
                    ArrayPool<int>.Shared.Return(pixels, clearArray: false);
                    pixels = null;
                }
                bitmap?.Dispose();
                lease?.Dispose();
                return null;
            }
            finally
            {
                result.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    internal bool QueueReference3DPlaybackRaster(StageControl stage)
    {
        return TryQueueReference3DPlaybackRaster(stage, out _);
    }

    private static Reference3DPlaybackViewKey CreateReference3DPlaybackViewKey(StageControl stage) =>
        new(
            stage.Width,
            stage.Height,
            stage.ReferenceDimension,
            stage.ReferenceProjection,
            stage.ReferenceProjectionBlend,
            stage.EffectiveReferenceYaw,
            stage.EffectiveReferencePitch,
            stage.ReferenceDistance,
            stage.ReferenceZoomScale,
            stage.ReferenceTargetX,
            stage.ReferenceTargetY,
            stage.ReferenceTargetZ);

    private static int GetReference3DPlaybackRasterWorkerLimit(int tileCount)
    {
        var configured = Environment.GetEnvironmentVariable(
            "VECTOR_PLAYBACK_RASTER_WORKERS");
        if (int.TryParse(configured, out var requested) && requested > 0)
        {
            return Math.Clamp(requested, 1, Math.Max(1, tileCount));
        }

        // Leave CPU headroom for the UI and the composition preloader. The
        // playback raster worker is already dedicated, so a full process-wide
        // worker fan-out makes frame latency less predictable on mid-range CPUs.
        return Math.Clamp(
            ParallelBatch.MaximumWorkerCount - 2,
            1,
            Math.Max(1, tileCount));
    }

    private bool TryPresentReference3DPlaybackRaster(
        StageControl stage,
        Graphics graphics,
        out RenderStats stats)
    {
        stats = default;

        var prefetchedFrame = _reference3DPlaybackRasterFrame;
        if (prefetchedFrame is not null && prefetchedFrame.Matches(stage))
        {
            Interlocked.Increment(ref _reference3DPlaybackRasterFrameMatchCount);
            var frame = prefetchedFrame;
            if (frame.TryTakeLease(out var prefetchedLease))
            {
                if (!PresentReference3DPlaybackLeaseToGdi(
                        graphics,
                        prefetchedLease,
                        stage.Width,
                        stage.Height))
                {
                    LastReference3DCpuRasterFallbackReason = "prefetched_gdi_present_failed";
                    InvalidateReference3DPlaybackRasterAfterPresentFailure(frame);
                    return false;
                }
                ApplyReference3DPlaybackRasterFrame(frame);
                _reference3DPlaybackRasterHasPresentedFrame = true;
                _reference3DPlaybackRasterLastStats = frame.Stats;
            }
            else if (frame.TryTakeBitmap(out var prefetchedBitmap))
            {
                if (!PresentReference3DPlaybackBitmapToGdi(
                        graphics,
                        prefetchedBitmap,
                        stage.Width,
                        stage.Height))
                {
                    LastReference3DCpuRasterFallbackReason = "prefetched_gdi_present_failed";
                    InvalidateReference3DPlaybackRasterAfterPresentFailure(frame);
                    return false;
                }
                ApplyReference3DPlaybackRasterFrame(frame);
                _reference3DPlaybackRasterHasPresentedFrame = true;
                _reference3DPlaybackRasterLastStats = frame.Stats;
            }
            else if (frame.TryTakePixels(out var pixels))
            {
                try
                {
                    ClearReference3DPlaybackPresentedBitmap();
                    if (!DrawReference3DCpuRasterToGdi(
                            graphics,
                            pixels,
                            frame.Width,
                            frame.Height,
                            stage.Width,
                            stage.Height))
                    {
                        LastReference3DCpuRasterFallbackReason = "prefetched_gdi_present_failed";
                        InvalidateReference3DPlaybackRasterAfterPresentFailure(frame);
                        return false;
                    }

                    ApplyReference3DPlaybackRasterFrame(frame);
                    _reference3DPlaybackRasterHasPresentedFrame = true;
                    _reference3DPlaybackRasterLastStats = frame.Stats;
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(pixels, clearArray: false);
                }
            }
            else if (_reference3DPlaybackRasterHasPresentedFrame)
            {
                if (!BlitReference3DFastPlaybackBitmapToGdi(
                        graphics,
                        stage.Width,
                        stage.Height))
                {
                    InvalidateReference3DPlaybackRasterAfterPresentFailure(frame);
                    return false;
                }
            }
            else
            {
                InvalidateReference3DPlaybackRasterAfterPresentFailure(frame);
                return false;
            }

            stats = _reference3DPlaybackRasterLastStats;
            LastReference3DCpuRasterFallbackReason = "used_prefetched_frame";
            return true;
        }

        if (prefetchedFrame is not null)
        {
            Interlocked.Increment(ref _reference3DPlaybackRasterFrameMismatchCount);
            Volatile.Write(
                ref _reference3DPlaybackRasterFrameLastMismatch,
                prefetchedFrame.MatchFailureReason);
        }

        var key = default(Reference3DPlaybackRasterRequestKey);
        if (!TryQueueReference3DPlaybackRaster(stage, out key)) return false;

        var worker = _reference3DPlaybackRasterWorker;
        if (worker is null) return false;
        if (worker.TryTakeLatest(
                key,
                _reference3DPlaybackRasterRequestSequence,
                _reference3DPlaybackRasterPresentedSequence,
                out var result))
        {
            try
            {
                if (result.TryTakeLease(out var resultLease))
                {
                    if (!PresentReference3DPlaybackLeaseToGdi(
                            graphics,
                            resultLease,
                            stage.Width,
                            stage.Height))
                    {
                        LastReference3DCpuRasterFallbackReason = "async_gdi_present_failed";
                        InvalidateReference3DPlaybackRasterAfterPresentFailure();
                        return false;
                    }
                }
                else if (result.TryTakeBitmap(out var resultBitmap))
                {
                    if (!PresentReference3DPlaybackBitmapToGdi(
                            graphics,
                            resultBitmap,
                            stage.Width,
                            stage.Height))
                    {
                        LastReference3DCpuRasterFallbackReason = "async_gdi_present_failed";
                        InvalidateReference3DPlaybackRasterAfterPresentFailure();
                        return false;
                    }
                }
                else if (result.TryTakePixels(out var pixels))
                {
                    ClearReference3DPlaybackPresentedBitmap();
                    if (!DrawReference3DCpuRasterToGdi(
                            graphics,
                            pixels,
                            result.Width,
                            result.Height,
                            stage.Width,
                            stage.Height))
                    {
                        LastReference3DCpuRasterFallbackReason = "async_gdi_present_failed";
                        InvalidateReference3DPlaybackRasterAfterPresentFailure();
                        return false;
                    }
                }
                else
                {
                    LastReference3DCpuRasterFallbackReason = "async_result_empty";
                    InvalidateReference3DPlaybackRasterAfterPresentFailure();
                    return false;
                }

                ApplyReference3DPlaybackRasterResult(result);
                _reference3DPlaybackRasterHasPresentedFrame = true;
                _reference3DPlaybackRasterLastStats =
                    CreateReference3DPlaybackRasterStats(stage, result.DrawnObjects);
            }
            finally
            {
                result.Dispose();
            }
        }
        else if (_reference3DPlaybackRasterHasPresentedFrame)
        {
            if (!BlitReference3DFastPlaybackBitmapToGdi(
                    graphics,
                    stage.Width,
                    stage.Height))
            {
                InvalidateReference3DPlaybackRasterAfterPresentFailure();
                return false;
            }
        }
        else
        {
            if (!DrawReference3DPlaybackBackgroundToGdi(graphics, stage.Width, stage.Height))
            {
                InvalidateReference3DPlaybackRasterAfterPresentFailure();
                return false;
            }
            _reference3DPlaybackRasterLastStats =
                CreateReference3DPlaybackRasterStats(stage, 0);
        }

        stats = _reference3DPlaybackRasterLastStats;
        LastReference3DCpuRasterFallbackReason =
            _reference3DPlaybackRasterHasPresentedFrame ? "used_async" : "queued";
        return true;
    }

    private void ApplyReference3DPlaybackRasterResult(
        Reference3DPlaybackRasterResult result)
    {
        LastReference3DCpuRasterWorkers = result.Workers;
        LastReference3DCpuRasterTiles = result.Tiles;
        LastReference3DCpuRasterCommands = result.Commands;
        LastReference3DCpuRasterMilliseconds = result.RasterMilliseconds;
        LastReference3DCpuRasterScale = result.RasterScale;
        LastReference3DPlaybackPresentedFrame = result.Key.Frame;
        _reference3DPlaybackRasterPresentedSequence = result.Sequence;
        _reference3DCpuRasterUsedThisFrame = true;
    }

    private void ApplyReference3DPlaybackRasterFrame(
        Reference3DPlaybackRasterFrame frame)
    {
        LastReference3DCpuRasterWorkers = frame.Workers;
        LastReference3DCpuRasterTiles = frame.Tiles;
        LastReference3DCpuRasterCommands = frame.Commands;
        LastReference3DCpuRasterMilliseconds = frame.RasterMilliseconds;
        LastReference3DCpuRasterScale = frame.RasterScale;
        LastReference3DPlaybackPresentedFrame = frame.Key.Frame;
        _reference3DPlaybackRasterPresentedSequence =
            Math.Max(_reference3DPlaybackRasterPresentedSequence, 1);
        _reference3DCpuRasterUsedThisFrame = true;
    }

    private bool DrawReference3DPlaybackBackgroundToGdi(
        Graphics graphics,
        int width,
        int height)
    {
        if (_reference3DPlaybackBackgroundBitmap is null
            || _reference3DPlaybackBackgroundBitmap.Width != width
            || _reference3DPlaybackBackgroundBitmap.Height != height)
        {
            return false;
        }

        var state = graphics.Save();
        try
        {
            graphics.ResetTransform();
            graphics.ResetClip();
            graphics.DrawImageUnscaled(_reference3DPlaybackBackgroundBitmap, 0, 0);
            return true;
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private bool PresentReference3DPlaybackBitmapToGdi(
        Graphics graphics,
        Bitmap bitmap,
        int destinationWidth,
        int destinationHeight)
    {
        try
        {
            ReplaceReference3DPlaybackPresentedBitmap(bitmap);
            return BlitReference3DPlaybackBitmapToGdi(
                graphics,
                _reference3DPlaybackPresentedBitmap!,
                _reference3DPlaybackPresentedBitmapGraphics!,
                destinationWidth,
                destinationHeight);
        }
        catch
        {
            ClearReference3DPlaybackPresentedBitmap();
            LastReference3DCpuRasterFallbackReason = "async_gdi_present_failed";
            return false;
        }
    }

    private bool PresentReference3DPlaybackLeaseToGdi(
        Graphics graphics,
        Reference3DPlaybackGdiRasterSurface.Lease lease,
        int destinationWidth,
        int destinationHeight)
    {
        try
        {
            ReplaceReference3DPlaybackPresentedBitmap(lease);
            return BlitReference3DPlaybackBitmapToGdi(
                graphics,
                lease.Bitmap,
                lease.PresentationGraphics,
                destinationWidth,
                destinationHeight);
        }
        catch
        {
            ClearReference3DPlaybackPresentedBitmap();
            LastReference3DCpuRasterFallbackReason = "async_gdi_present_failed";
            return false;
        }
    }

    private void ReplaceReference3DPlaybackPresentedBitmap(Bitmap bitmap)
    {
        ClearReference3DPlaybackPresentedBitmap();
        try
        {
            _reference3DPlaybackPresentedBitmap = bitmap;
            _reference3DPlaybackPresentedBitmapGraphics = Graphics.FromImage(bitmap);
        }
        catch
        {
            bitmap.Dispose();
            _reference3DPlaybackPresentedBitmap = null;
            throw;
        }
    }

    private void ReplaceReference3DPlaybackPresentedBitmap(
        Reference3DPlaybackGdiRasterSurface.Lease lease)
    {
        ClearReference3DPlaybackPresentedBitmap();
        _reference3DPlaybackPresentedBitmapLease = lease;
    }

    private void ClearReference3DPlaybackPresentedBitmap()
    {
        _reference3DPlaybackPresentedBitmapGraphics?.Dispose();
        _reference3DPlaybackPresentedBitmapGraphics = null;
        _reference3DPlaybackPresentedBitmap?.Dispose();
        _reference3DPlaybackPresentedBitmap = null;
        _reference3DPlaybackPresentedBitmapLease?.Dispose();
        _reference3DPlaybackPresentedBitmapLease = null;
    }

    private bool BlitReference3DFastPlaybackBitmapToGdi(
        Graphics graphics,
        int destinationWidth,
        int destinationHeight)
    {
        if (_reference3DPlaybackPresentedBitmapLease is { } presentedLease)
        {
            return BlitReference3DPlaybackBitmapToGdi(
                graphics,
                presentedLease.Bitmap,
                presentedLease.PresentationGraphics,
                destinationWidth,
                destinationHeight);
        }

        if (_reference3DPlaybackPresentedBitmap is { } presentedBitmap
            && _reference3DPlaybackPresentedBitmapGraphics is { } presentedGraphics)
        {
            return BlitReference3DPlaybackBitmapToGdi(
                graphics,
                presentedBitmap,
                presentedGraphics,
                destinationWidth,
                destinationHeight);
        }

        if (_reference3DFastPlaybackBitmap is null
            || _reference3DFastPlaybackBitmapGraphics is null
            || _reference3DFastPlaybackBitmap.Width <= 0
            || _reference3DFastPlaybackBitmap.Height <= 0
            || destinationWidth <= 0
            || destinationHeight <= 0)
        {
            LastReference3DCpuRasterFallbackReason = "async_bitmap_unavailable";
            return false;
        }

        return BlitReference3DPlaybackBitmapToGdi(
            graphics,
            _reference3DFastPlaybackBitmap,
            _reference3DFastPlaybackBitmapGraphics,
            destinationWidth,
            destinationHeight);
    }

    private bool BlitReference3DPlaybackBitmapToGdi(
        Graphics graphics,
        Bitmap sourceBitmap,
        Graphics sourceGraphics,
        int destinationWidth,
        int destinationHeight)
    {
        if (sourceBitmap.Width <= 0
            || sourceBitmap.Height <= 0
            || destinationWidth <= 0
            || destinationHeight <= 0)
        {
            LastReference3DCpuRasterFallbackReason = "async_bitmap_unavailable";
            return false;
        }

        graphics.ResetTransform();
        graphics.ResetClip();
        sourceGraphics.ResetTransform();
        sourceGraphics.ResetClip();
        var blitStarted = Stopwatch.GetTimestamp();
        var destinationHdc = IntPtr.Zero;
        var sourceHdc = IntPtr.Zero;
        try
        {
            sourceHdc = sourceGraphics.GetHdc();
            destinationHdc = graphics.GetHdc();
            if (TryBlitReference3DPlaybackBitmap(
                    destinationHdc,
                    sourceHdc,
                    sourceBitmap.Width,
                    sourceBitmap.Height,
                    destinationWidth,
                    destinationHeight))
            {
                return true;
            }

            LastReference3DCpuRasterFallbackReason = "async_gdi_blit_failed";
            return false;
        }
        finally
        {
            if (destinationHdc != IntPtr.Zero) graphics.ReleaseHdc(destinationHdc);
            if (sourceHdc != IntPtr.Zero) sourceGraphics.ReleaseHdc(sourceHdc);
            LastReference3DPlaybackBlitMilliseconds =
                Stopwatch.GetElapsedTime(blitStarted).TotalMilliseconds;
        }
    }

    private static bool TryBlitReference3DPlaybackBitmap(
        IntPtr destinationHdc,
        IntPtr sourceHdc,
        int sourceWidth,
        int sourceHeight,
        int destinationWidth,
        int destinationHeight)
    {
        if (sourceWidth == destinationWidth && sourceHeight == destinationHeight)
        {
            return BitBlt(
                destinationHdc,
                0,
                0,
                destinationWidth,
                destinationHeight,
                sourceHdc,
                0,
                0,
                Reference3DSourceCopyRasterOperation);
        }

        return StretchBlt(
            destinationHdc,
            0,
            0,
            destinationWidth,
            destinationHeight,
            sourceHdc,
            0,
            0,
            sourceWidth,
            sourceHeight,
            Reference3DSourceCopyRasterOperation);
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destinationHdc,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr sourceHdc,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(
        IntPtr destinationHdc,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight,
        IntPtr sourceHdc,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        uint rasterOperation);

    private static RenderStats CreateReference3DPlaybackRasterStats(
        StageControl stage,
        int drawnObjects)
    {
        var scene = stage.Scene;
        var visible = 0;
        long atoms = 0;
        for (var layer = scene.LayerCount - 1; layer >= 0; layer--)
        {
            if (!scene.ShouldRenderLayerContent(layer)) continue;
            var objects = stage.GetReference3DLayerObjects(layer);
            visible += objects.Length;
            foreach (var objectIndex in objects) atoms += scene.AtomCount[objectIndex];
        }

        return new RenderStats(visible, drawnObjects, atoms, 0, scene.ObjectCount, false);
    }

    private static bool CanUseReference3DPlaybackGdiRaster(
        Reference3DCpuRasterCommand[] commands)
    {
        if (commands.Length == 0) return false;
        foreach (var command in commands)
        {
            if (command.Contours.Length == 0
                || command.HasGradient && command.GradientKind != GradientKind.Linear
                || command.HasGradient && command.GradientStops.Length == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static GraphicsPath CreateReference3DPlaybackPath(
        IReadOnlyList<PointF[]> contours,
        bool fillOnly)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        AppendReference3DPlaybackPath(path, contours, fillOnly);
        return path;
    }

    private static void AppendReference3DPlaybackPath(
        GraphicsPath path,
        IReadOnlyList<PointF[]> contours,
        bool fillOnly)
    {
        foreach (var contour in contours)
        {
            if (contour.Length < (fillOnly ? 3 : 2)) continue;
            if (fillOnly)
            {
                path.AddPolygon(contour);
            }
            else
            {
                path.StartFigure();
                path.AddLines(contour);
            }
        }
    }

    private static bool CanReuseReference3DPlaybackPath(
        Reference3DCpuRasterCommand pathCommand,
        Reference3DCpuRasterCommand command)
    {
        // A fill command contains only closed contours. Reuse its closed path
        // for the immediately following stroke only when the two command
        // geometries are exactly the same; open contours must retain the
        // stroke-only path semantics.
        if (!pathCommand.Fill
            || command.Fill
            || pathCommand.Contours.Length != command.Contours.Length)
        {
            return false;
        }

        for (var contourIndex = 0;
             contourIndex < pathCommand.Contours.Length;
             contourIndex++)
        {
            var left = pathCommand.Contours[contourIndex];
            var right = command.Contours[contourIndex];
            if (left.Length != right.Length) return false;
            for (var pointIndex = 0; pointIndex < left.Length; pointIndex++)
            {
                if (!left[pointIndex].Equals(right[pointIndex])) return false;
            }
        }

        return true;
    }

    private static bool TryRasterizeReference3DPlaybackGdi(
        Reference3DPlaybackRasterRequest request,
        CancellationToken cancellation,
        out Reference3DPlaybackRasterResult result,
        Reference3DPlaybackGdiRasterSurface? surface = null,
        Reference3DPlaybackGdiBrushCache? sharedBrushCache = null)
    {
        result = null!;
        var rasterWidth = Math.Max(
            1,
            (int)MathF.Round(request.TargetWidth * request.RasterScale));
        var rasterHeight = Math.Max(
            1,
            (int)MathF.Round(request.TargetHeight * request.RasterScale));
        var pixelCountLong = (long)rasterWidth * rasterHeight;
        if (rasterWidth <= 0
            || rasterHeight <= 0
            || pixelCountLong <= 0
            || pixelCountLong > Reference3DCpuRasterMaximumPixels)
        {
            return false;
        }

        int[]? pixels = null;
        Bitmap? bitmap = null;
        Graphics? graphics = null;
        Reference3DPlaybackGdiRasterSurface.Lease? lease = null;
        var ownsBitmap = false;
        var ownsGraphics = false;
        var brushCache = surface?.BrushCache
            ?? sharedBrushCache
            ?? new Reference3DPlaybackGdiBrushCache();
        var ownsBrushCache = surface is null && sharedBrushCache is null;
        var commandIndex = -1;
        var phase = "setup";
        try
        {
            var pixelCount = (int)pixelCountLong;
            if (surface is null)
            {
                bitmap = new Bitmap(
                    rasterWidth,
                    rasterHeight,
                    PixelFormat.Format32bppPArgb);
                ownsBitmap = true;
                graphics = Graphics.FromImage(bitmap);
                ownsGraphics = true;
            }
            else
            {
                lease = surface.Acquire(rasterWidth, rasterHeight);
                bitmap = lease.Bitmap;
                graphics = lease.Graphics;
            }

            var renderBitmap = bitmap;
            var renderGraphics = graphics;
            if (renderBitmap is null || renderGraphics is null) return false;

            {
                renderGraphics.PageUnit = GraphicsUnit.Pixel;
                renderGraphics.CompositingMode = CompositingMode.SourceOver;
                renderGraphics.CompositingQuality = CompositingQuality.HighSpeed;
                renderGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                renderGraphics.PixelOffsetMode = PixelOffsetMode.Half;
                renderGraphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                renderGraphics.ResetTransform();

                if (request.BackgroundBitmap is { } backgroundBitmap
                    && backgroundBitmap.Width == request.TargetWidth
                    && backgroundBitmap.Height == request.TargetHeight)
                {
                    phase = "background";
                    renderGraphics.DrawImage(
                        backgroundBitmap,
                        new Rectangle(0, 0, rasterWidth, rasterHeight),
                        new Rectangle(0, 0, request.TargetWidth, request.TargetHeight),
                        GraphicsUnit.Pixel);
                }
                else
                {
                    phase = "background";
                    pixels = ArrayPool<int>.Shared.Rent(pixelCount);
                    CopyReference3DPlaybackBackground(
                        request.Key.BackgroundPixels,
                        request.TargetWidth,
                        request.TargetHeight,
                        pixels,
                        rasterWidth,
                        rasterHeight);
                    cancellation.ThrowIfCancellationRequested();
                    CopyReference3DPlaybackPixelsToBitmap(
                        renderBitmap,
                        pixels,
                        rasterWidth,
                        rasterHeight);
                }

                var rasterStarted = Stopwatch.GetTimestamp();
                GraphicsPath? path = null;
                Reference3DCpuRasterCommand? pathCommand = null;
                try
                {
                    foreach (var command in request.Commands)
                    {
                        commandIndex++;
                        phase = "path";
                        cancellation.ThrowIfCancellationRequested();
                        if (command.SingleClosedContour)
                        {
                            phase = "brush";
                            var polygonBrush = brushCache.GetBrush(command);
                            if (command.Fill)
                            {
                                phase = "fill";
                                renderGraphics.FillPolygon(
                                    polygonBrush,
                                    command.Contours[0],
                                    FillMode.Alternate);
                            }
                            else
                            {
                                phase = "pen";
                                using var polygonPen = new Pen(
                                    polygonBrush,
                                    command.StrokeWidth)
                                {
                                    LineJoin = LineJoin.Round,
                                    StartCap = LineCap.Round,
                                    EndCap = LineCap.Round
                                };
                                phase = "stroke";
                                renderGraphics.DrawPolygon(
                                    polygonPen,
                                    command.Contours[0]);
                            }
                            pathCommand = null;
                            continue;
                        }
                        if (path is null
                            || pathCommand is null
                            || !CanReuseReference3DPlaybackPath(pathCommand, command))
                        {
                            path ??= new GraphicsPath(FillMode.Alternate);
                            path.Reset();
                            path.FillMode = FillMode.Alternate;
                            AppendReference3DPlaybackPath(
                                path,
                                command.Contours,
                                command.Fill);
                            pathCommand = command;
                        }
                        if (path.PointCount == 0) continue;
                        phase = "brush";
                        var brush = brushCache.GetBrush(command);
                        if (command.Fill)
                        {
                            phase = "fill";
                            renderGraphics.FillPath(brush, path);
                            continue;
                        }

                        phase = "pen";
                        using var pen = new Pen(brush, command.StrokeWidth)
                        {
                            LineJoin = LineJoin.Round,
                            StartCap = LineCap.Round,
                            EndCap = LineCap.Round
                        };
                        phase = "stroke";
                        renderGraphics.DrawPath(pen, path);
                    }
                }
                finally
                {
                    path?.Dispose();
                }

                phase = "publish";
                Reference3DPlaybackGdiRasterSurface.Lease? renderLease = null;
                if (surface is not null)
                {
                    var publishedLease = lease
                        ?? throw new InvalidOperationException(
                            "The GDI playback surface lease was lost before publish.");
                    publishedLease.PrepareForPresentation();
                    renderLease = publishedLease;
                    lease = null;
                }
                else
                {
                    if (ownsGraphics)
                    {
                        graphics?.Dispose();
                        graphics = null;
                        ownsGraphics = false;
                    }
                    ownsBitmap = false;
                }
                var tilesX = (rasterWidth + Reference3DCpuRasterTileSize - 1)
                    / Reference3DCpuRasterTileSize;
                var tilesY = (rasterHeight + Reference3DCpuRasterTileSize - 1)
                    / Reference3DCpuRasterTileSize;
                result = new Reference3DPlaybackRasterResult(
                    request.Key,
                    pixels: null,
                    bitmap: renderLease is null ? renderBitmap : null,
                    lease: renderLease,
                    pixelCount: pixelCount,
                    width: rasterWidth,
                    height: rasterHeight,
                    rasterScale: request.RasterScale,
                    drawnObjects: request.DrawnObjects,
                    workers: 1,
                    tiles: checked(tilesX * tilesY),
                    commands: request.Commands.Length,
                    rasterMilliseconds: Stopwatch.GetElapsedTime(rasterStarted).TotalMilliseconds,
                    sequence: request.Sequence);
                return true;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            var hasDiagnosticCommand = commandIndex >= 0
                && commandIndex < request.Commands.Length;
            var diagnosticCommand = hasDiagnosticCommand
                ? request.Commands[commandIndex]
                : null;
            var gradientDetails = diagnosticCommand is null
                ? "start=n/a end=n/a"
                : $"start={diagnosticCommand.GradientStartScreen} "
                    + $"end={diagnosticCommand.GradientEndScreen}";
            Volatile.Write(
                ref _lastReference3DPlaybackGdiRasterFailure,
                $"command={commandIndex}:{phase}:{exception.GetType().Name}:{exception.Message} "
                + gradientDetails);
            return false;
        }
        finally
        {
            if (ownsGraphics) graphics?.Dispose();
            if (ownsBitmap) bitmap?.Dispose();
            lease?.Dispose();
            if (ownsBrushCache) brushCache.Dispose();
            if (pixels is not null)
            {
                ArrayPool<int>.Shared.Return(pixels, clearArray: false);
            }
        }
    }

    private static Brush CreateReference3DPlaybackGdiBrush(
        Reference3DCpuRasterCommand command)
    {
        if (!command.HasGradient)
        {
            return new SolidBrush(Color.FromArgb(command.SolidArgb));
        }

        var stops = command.GradientStops;
        if (stops.Length == 0)
        {
            return new SolidBrush(Color.FromArgb(command.SolidArgb));
        }

        var firstPosition = stops[0].Position;
        var lastPosition = stops[^1].Position;
        var positionLength = lastPosition - firstPosition;
        if (!float.IsFinite(firstPosition)
            || !float.IsFinite(lastPosition)
            || !float.IsFinite(positionLength)
            || positionLength <= 0.000001f)
        {
            return new SolidBrush(Color.FromArgb(stops[^1].Argb));
        }

        var colors = new Color[stops.Length];
        var positions = new float[stops.Length];
        for (var index = 0; index < stops.Length; index++)
        {
            colors[index] = Color.FromArgb(stops[index].Argb);
            positions[index] = Math.Clamp(
                (stops[index].Position - firstPosition) / positionLength,
                0f,
                1f);
        }
        positions[0] = 0f;
        positions[^1] = 1f;

        // GDI+ rejects a zero-length gradient axis. Perspective projection can
        // collapse an authored axis even when the source gradient is valid;
        // keep that command on the fast path with the same bounded solid
        // fallback used by the CPU rasterizer.
        var axisX = command.GradientEndScreen.X - command.GradientStartScreen.X;
        var axisY = command.GradientEndScreen.Y - command.GradientStartScreen.Y;
        var axisLengthSquared = axisX * axisX + axisY * axisY;
        if (!float.IsFinite(axisLengthSquared) || axisLengthSquared <= 0.000001f)
        {
            return new SolidBrush(Color.FromArgb(stops[^1].Argb));
        }

        LinearGradientBrush? brush = null;
        try
        {
            brush = new LinearGradientBrush(
                new PointF(command.GradientStartScreen.X, command.GradientStartScreen.Y),
                new PointF(command.GradientEndScreen.X, command.GradientEndScreen.Y),
                colors[0],
                colors[^1]);
            brush.WrapMode = WrapMode.Clamp;
            brush.InterpolationColors = new ColorBlend
            {
                Colors = colors,
                Positions = positions
            };
            return brush;
        }
        catch (ArgumentException)
        {
            brush?.Dispose();
            return new SolidBrush(colors[^1]);
        }
    }

    private static void CopyReference3DPlaybackPixelsToBitmap(
        Bitmap bitmap,
        int[] pixels,
        int width,
        int height)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            for (var row = 0; row < height; row++)
            {
                var bitmapRow = data.Stride >= 0 ? row : height - row - 1;
                Marshal.Copy(
                    pixels,
                    row * width,
                    IntPtr.Add(data.Scan0, bitmapRow * data.Stride),
                    width);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Reference3DPlaybackRasterResult? RasterizeReference3DPlayback(
        Reference3DPlaybackRasterRequest request,
        CancellationToken cancellation,
        Reference3DPlaybackGdiRasterSurface? gdiSurface = null,
        Reference3DPlaybackGdiBrushCache? gdiBrushCache = null)
    {
        var rasterWidth = Math.Max(
            1,
            (int)MathF.Round(request.TargetWidth * request.RasterScale));
        var rasterHeight = Math.Max(
            1,
            (int)MathF.Round(request.TargetHeight * request.RasterScale));
        var pixelCountLong = (long)rasterWidth * rasterHeight;
        if (rasterWidth <= 0
            || rasterHeight <= 0
            || pixelCountLong <= 0
            || pixelCountLong > Reference3DCpuRasterMaximumPixels)
        {
            return null;
        }

        if (request.AllowGdiRaster
            && Environment.GetEnvironmentVariable("VECTOR_DISABLE_GDI_PLAYBACK_RASTER") != "1"
            && CanUseReference3DPlaybackGdiRaster(request.Commands)
            && TryRasterizeReference3DPlaybackGdi(
                request,
                cancellation,
                out var gdiResult,
                gdiSurface,
                gdiBrushCache))
        {
            return gdiResult;
        }

        // The GDI+ playback path does not use fill scanline tables. Build
        // them only when it fails or is disabled and the tiled CPU fallback
        // is actually needed.
        foreach (var command in request.Commands)
        {
            cancellation.ThrowIfCancellationRequested();
            command.EnsureGradientLookup();
            command.EnsureFillScanlines();
        }

        var pixelCount = (int)pixelCountLong;
        int[]? pixels = ArrayPool<int>.Shared.Rent(pixelCount);
        int[]? outputPixels = null;
        try
        {
            CopyReference3DPlaybackBackground(
                request.Key.BackgroundPixels,
                request.TargetWidth,
                request.TargetHeight,
                pixels,
                rasterWidth,
                rasterHeight);
            cancellation.ThrowIfCancellationRequested();

            var tilesX = (rasterWidth + Reference3DCpuRasterTileSize - 1)
                / Reference3DCpuRasterTileSize;
            var tilesY = (rasterHeight + Reference3DCpuRasterTileSize - 1)
                / Reference3DCpuRasterTileSize;
            var tileCount = checked(tilesX * tilesY);
            var tileBins = request.TileBins;
            if (tileBins is null
                || !tileBins.Matches(
                    rasterWidth,
                    rasterHeight,
                    tilesX,
                    tilesY,
                    request.Commands))
            {
                tileBins = TryBuildReference3DCpuRasterTileBins(
                    request.Commands,
                    rasterWidth,
                    rasterHeight,
                    rasterScale: 1f)
                    ?? throw new InvalidOperationException("cpu_raster_bin_build_failed");
            }

            var workers = ParallelBatch.WorkerCount(
                tileCount,
                1,
                maxWorkers: GetReference3DPlaybackRasterWorkerLimit(tileCount));
            var rasterStarted = Stopwatch.GetTimestamp();
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellation,
                MaxDegreeOfParallelism = workers
            };
            Parallel.For(
                0,
                tileCount,
                parallelOptions,
                () => ArrayPool<float>.Shared.Rent(Reference3DCpuRasterTileSize),
                (tileIndex, _, coverage) =>
                {
                    var tileX = tileIndex % tilesX;
                    var tileY = tileIndex / tilesX;
                    var left = tileX * Reference3DCpuRasterTileSize;
                    var top = tileY * Reference3DCpuRasterTileSize;
                    var right = Math.Min(
                        rasterWidth,
                        left + Reference3DCpuRasterTileSize);
                    var bottom = Math.Min(
                        rasterHeight,
                        top + Reference3DCpuRasterTileSize);
                    var commandStart = tileBins.CommandOffsets[tileIndex];
                    var commandEnd = tileBins.CommandOffsets[tileIndex + 1];
                    RasterReference3DCpuTile(
                        pixels,
                        rasterWidth,
                        request.Commands,
                        tileBins.CommandIndices,
                        commandStart,
                        commandEnd,
                        left,
                        top,
                        right,
                        bottom,
                         coverage);
                    return coverage;
                },
                coverage => ArrayPool<float>.Shared.Return(coverage, clearArray: false));

            var outputWidth = rasterWidth;
            var outputHeight = rasterHeight;
            var outputPixelCount = pixelCount;
            outputPixels = pixels;
            if (rasterWidth != request.TargetWidth || rasterHeight != request.TargetHeight)
            {
                var outputPixelCountLong = (long)request.TargetWidth * request.TargetHeight;
                if (request.TargetWidth <= 0
                    || request.TargetHeight <= 0
                    || outputPixelCountLong <= 0
                    || outputPixelCountLong > Reference3DCpuRasterMaximumPixels)
                {
                    throw new InvalidOperationException("cpu_raster_output_size_limit");
                }

                outputPixelCount = (int)outputPixelCountLong;
                outputWidth = request.TargetWidth;
                outputHeight = request.TargetHeight;
                outputPixels = ArrayPool<int>.Shared.Rent(outputPixelCount);
                UpscaleReference3DPlaybackPixels(
                    pixels,
                    rasterWidth,
                    rasterHeight,
                    outputPixels,
                    outputWidth,
                    outputHeight);
                ArrayPool<int>.Shared.Return(pixels, clearArray: false);
                pixels = null;
            }

            return new Reference3DPlaybackRasterResult(
                request.Key,
                pixels: outputPixels!,
                bitmap: null,
                lease: null,
                pixelCount: outputPixelCount,
                width: outputWidth,
                height: outputHeight,
                rasterScale: request.RasterScale,
                drawnObjects: request.DrawnObjects,
                workers: workers,
                tiles: tileCount,
                commands: request.Commands.Length,
                rasterMilliseconds: Stopwatch.GetElapsedTime(rasterStarted).TotalMilliseconds,
                sequence: request.Sequence);
        }
        catch (OperationCanceledException)
        {
            if (outputPixels is not null) ArrayPool<int>.Shared.Return(outputPixels, clearArray: false);
            if (pixels is not null && !ReferenceEquals(pixels, outputPixels))
            {
                ArrayPool<int>.Shared.Return(pixels, clearArray: false);
            }
            return null;
        }
        catch
        {
            if (outputPixels is not null) ArrayPool<int>.Shared.Return(outputPixels, clearArray: false);
            if (pixels is not null && !ReferenceEquals(pixels, outputPixels))
            {
                ArrayPool<int>.Shared.Return(pixels, clearArray: false);
            }
            throw;
        }
    }

    private static void UpscaleReference3DPlaybackPixels(
        int[] source,
        int sourceWidth,
        int sourceHeight,
        int[] destination,
        int destinationWidth,
        int destinationHeight)
    {
        if (sourceWidth == destinationWidth && sourceHeight == destinationHeight)
        {
            Array.Copy(source, destination, checked(destinationWidth * destinationHeight));
            return;
        }

        var sourceMaxX = Math.Max(0, sourceWidth - 1);
        var sourceMaxY = Math.Max(0, sourceHeight - 1);
        var destinationMaxX = Math.Max(1, destinationWidth - 1);
        var destinationMaxY = Math.Max(1, destinationHeight - 1);
        for (var y = 0; y < destinationHeight; y++)
        {
            var sourceYFixed = (long)y * sourceMaxY * 65536L / destinationMaxY;
            var sourceY = Math.Min(sourceMaxY, (int)(sourceYFixed >> 16));
            var yWeight = (int)(sourceYFixed & 0xffff);
            var nextSourceY = Math.Min(sourceMaxY, sourceY + 1);
            var topRow = sourceY * sourceWidth;
            var bottomRow = nextSourceY * sourceWidth;
            var destinationRow = y * destinationWidth;
            for (var x = 0; x < destinationWidth; x++)
            {
                var sourceXFixed = (long)x * sourceMaxX * 65536L / destinationMaxX;
                var sourceX = Math.Min(sourceMaxX, (int)(sourceXFixed >> 16));
                var xWeight = (int)(sourceXFixed & 0xffff);
                var nextSourceX = Math.Min(sourceMaxX, sourceX + 1);
                var top = LerpReference3DArgb(
                    source[topRow + sourceX],
                    source[topRow + nextSourceX],
                    xWeight);
                var bottom = LerpReference3DArgb(
                    source[bottomRow + sourceX],
                    source[bottomRow + nextSourceX],
                    xWeight);
                destination[destinationRow + x] = LerpReference3DArgb(
                    top,
                    bottom,
                    yWeight);
            }
        }
    }

    private static int LerpReference3DArgb(int first, int second, int weight)
    {
        var inverse = 65536 - weight;
        var a = (((first >> 24) & 0xff) * inverse
            + ((second >> 24) & 0xff) * weight + 32768) >> 16;
        var r = (((first >> 16) & 0xff) * inverse
            + ((second >> 16) & 0xff) * weight + 32768) >> 16;
        var g = (((first >> 8) & 0xff) * inverse
            + ((second >> 8) & 0xff) * weight + 32768) >> 16;
        var b = ((first & 0xff) * inverse + (second & 0xff) * weight + 32768) >> 16;
        return a << 24 | r << 16 | g << 8 | b;
    }

    private static void CopyReference3DPlaybackBackground(
        int[] source,
        int sourceWidth,
        int sourceHeight,
        int[] destination,
        int destinationWidth,
        int destinationHeight)
    {
        if (sourceWidth == destinationWidth && sourceHeight == destinationHeight)
        {
            Array.Copy(source, destination, checked(destinationWidth * destinationHeight));
            return;
        }

        for (var y = 0; y < destinationHeight; y++)
        {
            var sourceY = Math.Clamp(
                (int)((long)y * sourceHeight / destinationHeight),
                0,
                sourceHeight - 1);
            var sourceRow = sourceY * sourceWidth;
            var destinationRow = y * destinationWidth;
            for (var x = 0; x < destinationWidth; x++)
            {
                var sourceX = Math.Clamp(
                    (int)((long)x * sourceWidth / destinationWidth),
                    0,
                    sourceWidth - 1);
                destination[destinationRow + x] = source[sourceRow + sourceX];
            }
        }
    }

    private void ClearReference3DPlaybackRasterWorker()
    {
        _reference3DPlaybackRasterWorker?.Dispose();
        _reference3DPlaybackRasterWorker = null;
        _reference3DPlaybackRasterFrame?.Dispose();
        _reference3DPlaybackRasterFrame = null;
        _reference3DPlaybackRasterRequestKey = default;
        _reference3DPlaybackRasterRequestKeyValid = false;
        _reference3DPlaybackRasterHasPresentedFrame = false;
        _reference3DPlaybackRasterLastStats = default;
        ClearReference3DPlaybackPresentedBitmap();
        _reference3DPlaybackGdiRasterSurface?.Dispose();
        _reference3DPlaybackGdiRasterSurface = null;
    }
}
