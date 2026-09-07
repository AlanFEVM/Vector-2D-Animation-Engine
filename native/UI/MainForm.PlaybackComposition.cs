using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private const int PlaybackCompositionPrefetchCapacity = 8;
    private const int PlaybackCompositionPrefetchHorizon =
        PlaybackCompositionPrefetchCapacity * 2;
    // Keep composition and raster-command preparation as separate bounded
    // stages. Running multiple full composition workers duplicates snapshot
    // restoration and competes with the raster worker for the same CPU.
    private const int PlaybackCompositionWorkerCount = 1;
    private static readonly int PlaybackRasterPreparationWorkerCount =
        ResolvePlaybackRasterPreparationWorkerCount();
    private const int PlaybackRasterPreparationMaximumWorkers = 2;
    private const uint PlaybackTimerResolutionMilliseconds = 1;
    // Match the existing playback timer cadence. Posting at 2 ms can keep a
    // busy WinForms queue permanently non-empty and starve paint/input work.
    internal const int PlaybackSchedulerIntervalMilliseconds = 8;
    private const int PlaybackPreloaderWaitMilliseconds = 50;
    private bool _playbackTimerResolutionActive;
    private PlaybackSchedulerState? _playbackSchedulerState;
    private Thread? _playbackSchedulerThread;
    private PlaybackCompositionPreloader? _playbackCompositionPreloader;
    private bool _playbackCompositionSceneActive;
    private string _playbackCompositionPreloadStatus = "inactive";

    private static int ResolvePlaybackRasterPreparationWorkerCount()
    {
        var configured = Environment.GetEnvironmentVariable(
            "VECTOR_PLAYBACK_RASTER_PREPARATION_WORKERS");
        if (int.TryParse(configured, out var requested) && requested > 0)
        {
            return Math.Clamp(requested, 1, 2);
        }

        // Keep one composition worker available while two independent
        // preparation stages overlap geometry setup and GDI rasterization.
        return Environment.ProcessorCount >= 4 ? 2 : 1;
    }

    internal PlaybackCompositionPreloadMetrics PlaybackCompositionMetrics =>
        _playbackCompositionPreloader?.Metrics ?? default;

    internal bool PlaybackCompositionPreloadActive =>
        _playbackCompositionPreloader is not null;

    internal string PlaybackCompositionPreloadStatus => _playbackCompositionPreloadStatus;

    private void StartPlaybackCompositionPreload()
    {
        StopPlaybackCompositionPreload();
        _playbackCompositionPreloadStatus = "checking";
        if (Environment.GetEnvironmentVariable("VECTOR_DISABLE_PLAYBACK_PREFETCH") == "1")
        {
            _playbackCompositionPreloadStatus = "disabled_by_environment";
            return;
        }
        if (!IsSceneCompositionContext())
        {
            _playbackCompositionPreloadStatus = "not_scene_composition";
            return;
        }

        var definition = ActiveScene();
        if (definition is null)
        {
            _playbackCompositionPreloadStatus = "no_scene";
            return;
        }

        if (definition.Layers.Any(layer =>
                layer.Kind == SceneLayerKind.Mask
                || !string.IsNullOrWhiteSpace(layer.MaskLayerId))
            || _stage.SceneCompositionMaskClips.Count > 0)
        {
            _playbackCompositionPreloadStatus = definition.Layers.Any(layer =>
                layer.Kind == SceneLayerKind.Mask
                || !string.IsNullOrWhiteSpace(layer.MaskLayerId))
                ? "mask_layers"
                : "composition_mask_clips";
            return;
        }

        try
        {
            // A restart snapshot gives the worker an immutable source graph.
            // Building directly from the live WinForms model would race with
            // timeline binding and would make stopping playback unsafe.
            var snapshot = _project.CreateRestartSnapshot();
            var preparationState = _stage.CaptureReference3DPlaybackPreparationState();
            _playbackCompositionPreloader = new PlaybackCompositionPreloader(
                snapshot,
                definition.Id,
                _frame,
                _playbackSettings.StartFrame,
                _playbackSettings.EndFrame,
                _playbackSettings.LoopPlayback,
                _playbackSettings.Fps,
                PlaybackCompositionPrefetchCapacity,
                preparationState);
            _playbackCompositionPreloader.Start();
            // Keep the editable current frame visible until the asynchronous
            // preloader publishes a replacement. Playback startup must never
            // block the WinForms thread waiting for snapshot restoration.
            _playbackCompositionPreloadStatus = "started";
        }
        catch (Exception exception)
        {
            _playbackCompositionPreloadStatus = "start_failed";
            AppLog.Warn($"Playback composition prefetch skipped: {exception.Message}");
            StopPlaybackCompositionPreload();
        }
    }

    private bool TryApplyPlaybackComposition(int frame, out int appliedFrame)
    {
        appliedFrame = frame;
        var preloader = _playbackCompositionPreloader;
        var definition = ActiveScene();
        if (preloader is null) return false;
        if (preloader.Failed)
        {
            StopPlaybackCompositionPreload();
            _playbackCompositionPreloadStatus = "prefetch_failed";
            return false;
        }
        if (definition is null)
        {
            StopPlaybackCompositionPreload();
            _playbackCompositionPreloadStatus = "apply_no_scene";
            return false;
        }
        if (!preloader.Matches(
                definition.Id,
                _playbackSettings.StartFrame,
                _playbackSettings.EndFrame,
                _playbackSettings.LoopPlayback,
                _playbackSettings.Fps))
        {
            var mismatch = preloader.DescribeMismatch(
                definition.Id,
                _playbackSettings.StartFrame,
                _playbackSettings.EndFrame,
                _playbackSettings.LoopPlayback,
                _playbackSettings.Fps);
            StopPlaybackCompositionPreload();
            _playbackCompositionPreloadStatus = mismatch;
            return false;
        }
        // Tell the worker about the exact frame the UI now wants before
        // checking the ready queue. If that frame missed its deadline, the
        // preloader may return the nearest completed future frame so playback
        // keeps moving instead of freezing on an obsolete frame.
        preloader.RequestFrame(frame);
        if (!preloader.TryTake(
                frame,
                allowForwardDrop: true,
                out var prepared,
                out appliedFrame))
        {
            return false;
        }

        try
        {
            _scene = prepared.Scene;
            _sceneCompositionResult = prepared.Composition;
            _stage.SetReference3DPlaybackComposition(
                prepared.Scene,
                prepared.Composition,
                prepared.RasterPreparation,
                prepared.RasterFrame);
            _playbackCompositionSceneActive = true;
            preloader.NotifyCurrentFrame(appliedFrame);
            return true;
        }
        catch
        {
            // The prepared frame is transferred to Stage only after the whole
            // binding succeeds. Release it when a UI-side handoff fails.
            prepared.Dispose();
            throw;
        }
    }

    private void RestoreEditableCompositionScene()
    {
        if (!_playbackCompositionSceneActive) return;
        _playbackCompositionSceneActive = false;
        _scene = _sceneEditStage;
        _stage.Scene = _sceneEditStage;
    }

    private void StopPlaybackCompositionPreload()
    {
        if (_playbackCompositionPreloader is not null)
        {
            _playbackCompositionPreloadStatus = "stopped";
        }
        _playbackCompositionPreloader?.Dispose();
        _playbackCompositionPreloader = null;
        RestoreEditableCompositionScene();
    }

    private void BeginPlaybackTimerResolution()
    {
        if (_playbackTimerResolutionActive || !OperatingSystem.IsWindows()) return;
        if (timeBeginPeriod(PlaybackTimerResolutionMilliseconds) == 0)
        {
            _playbackTimerResolutionActive = true;
        }
    }

    private void EndPlaybackTimerResolution()
    {
        if (!_playbackTimerResolutionActive) return;
        timeEndPeriod(PlaybackTimerResolutionMilliseconds);
        _playbackTimerResolutionActive = false;
    }

    private void StartPlaybackScheduler()
    {
        StopPlaybackScheduler();
        _timer.Stop();

        var state = new PlaybackSchedulerState(this);
        _playbackSchedulerState = state;
        var thread = new Thread(() => RunPlaybackScheduler(state))
        {
            IsBackground = true,
            Name = "Editor playback scheduler",
            Priority = ThreadPriority.Normal
        };
        _playbackSchedulerThread = thread;
        thread.Start();
    }

    private void StopPlaybackScheduler()
    {
        var state = _playbackSchedulerState;
        _playbackSchedulerState = null;
        _playbackSchedulerThread = null;
        if (state is not null)
        {
            state.Cancellation.Cancel();
            if (state.Completion.Task.IsCompleted)
            {
                state.Cancellation.Dispose();
            }
            else
            {
                _ = state.Completion.Task.ContinueWith(
                    _ => state.Cancellation.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }
        if (!IsDisposed && !Disposing)
        {
            _timer.Interval = IdleTimerIntervalMs;
            _timer.Start();
        }
    }

    private void RunPlaybackScheduler(PlaybackSchedulerState state)
    {
        try
        {
            var intervalTicks = Math.Max(
                1L,
                (long)Math.Round(
                    Stopwatch.Frequency
                    * (PlaybackSchedulerIntervalMilliseconds / 1000d)));
            var nextTickAt = Stopwatch.GetTimestamp();
            while (!state.Cancellation.IsCancellationRequested)
            {
                PostPlaybackTick(state);
                nextTickAt += intervalTicks;
                var remainingTicks = nextTickAt - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    // Do not accumulate scheduler debt after a busy UI
                    // callback; the next posted tick should represent the
                    // current clock rather than a burst of stale ticks.
                    nextTickAt = Stopwatch.GetTimestamp();
                    Thread.Yield();
                    continue;
                }

                var remainingMilliseconds = (int)Math.Clamp(
                    remainingTicks * 1000L / Stopwatch.Frequency,
                    0L,
                    50L);
                if (state.Cancellation.Token.WaitHandle.WaitOne(
                        Math.Max(1, remainingMilliseconds)))
                {
                    break;
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutdown can dispose the cancellation source after the thread
            // has observed its stop request.
        }
        finally
        {
            state.Completion.TrySetResult(true);
        }
    }

    private void PostPlaybackTick(PlaybackSchedulerState state)
    {
        if (!state.TickGate.TryReserve()) return;
        try
        {
            BeginInvoke(state.UiTickCallback);
        }
        catch (ObjectDisposedException)
        {
            state.TickGate.CancelReservation();
        }
        catch (InvalidOperationException)
        {
            state.TickGate.CancelReservation();
        }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint periodMilliseconds);

    private sealed class PlaybackCompositionPreloader : IDisposable
    {
        private readonly ProjectRestartSnapshot _snapshot;
        private readonly string _sceneId;
        private readonly int _startFrame;
        private readonly int _endFrame;
        private readonly bool _loop;
        private readonly decimal _fps;
        private readonly int _capacity;
        private readonly Reference3DPlaybackPreparationState _preparationState;
        private readonly BlockingCollection<PendingPlaybackComposition> _pending;
        private readonly ConcurrentDictionary<int, PreparedPlaybackComposition> _ready = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ManualResetEventSlim _wake = new(false);
        private Task[] _workers = [];
        private Task _workerCompletion = Task.CompletedTask;
        private int _currentFrame;
        private int _requestedFrame;
        private long _builtCount;
        private long _takenCount;
        private long _missCount;
        private long _droppedCount;
        private long _restoreMillisecondsBits;
        private long _buildMillisecondsMicroseconds;
        private long _compositionMillisecondsMicroseconds;
        private long _rasterPreparationMillisecondsMicroseconds;
        private long _renderPlanMillisecondsMicroseconds;
        private long _commandPreparationMillisecondsMicroseconds;
        private long _prefetchedRasterFrameCount;
        private long _prefetchedRasterMillisecondsMicroseconds;
        private long _lastBuildMillisecondsBits;
        private int _lastBuiltFrame = -1;
        private int _primeCurrentFrame = 1;
        private int _disposed;
        private int _failed;
        private int _resourcesDisposed;
        private readonly ConcurrentDictionary<int, byte> _building = new();

        public PlaybackCompositionPreloader(
            ProjectRestartSnapshot snapshot,
            string sceneId,
            int currentFrame,
            int startFrame,
            int endFrame,
            bool loop,
            decimal fps,
            int capacity,
            Reference3DPlaybackPreparationState preparationState)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _sceneId = sceneId ?? string.Empty;
            _startFrame = Math.Max(0, startFrame);
            _endFrame = Math.Max(_startFrame, endFrame);
            _loop = loop;
            _fps = Math.Max(1m, fps);
            _capacity = Math.Max(1, capacity);
            _preparationState = preparationState;
            _pending = new BlockingCollection<PendingPlaybackComposition>(
                new ConcurrentQueue<PendingPlaybackComposition>(),
                _capacity);
            _currentFrame = Math.Clamp(currentFrame, _startFrame, _endFrame);
            _requestedFrame = _currentFrame;
        }

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _workers = Enumerable
                .Range(0, PlaybackCompositionWorkerCount)
                .Select(_ => Task.Factory.StartNew(
                    RunComposition,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .Concat(Enumerable
                    .Range(0, PlaybackRasterPreparationWorkerCount)
                    .Select(_ => Task.Factory.StartNew(
                        RunRasterPreparation,
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default)))
                .ToArray();
            _workerCompletion = Task.WhenAll(_workers);
            _wake.Set();
        }

        public bool Failed => Volatile.Read(ref _failed) != 0;

        public bool Matches(
            string sceneId,
            int startFrame,
            int endFrame,
            bool loop,
            decimal fps)
        {
            return string.Equals(_sceneId, sceneId, StringComparison.Ordinal)
                && _startFrame == Math.Max(0, startFrame)
                && _endFrame == Math.Max(_startFrame, endFrame)
                && _loop == loop
                && _fps == Math.Max(1m, fps);
        }

        public string DescribeMismatch(
            string sceneId,
            int startFrame,
            int endFrame,
            bool loop,
            decimal fps)
        {
            return "mismatch:"
                + $"scene={string.Equals(_sceneId, sceneId, StringComparison.Ordinal)} "
                + $"range={_startFrame == Math.Max(0, startFrame)}/{_endFrame == Math.Max(_startFrame, endFrame)} "
                + $"loop={_loop == loop} fps={_fps == Math.Max(1m, fps)}";
        }

        public PlaybackCompositionPreloadMetrics Metrics => new(
            _ready.Count,
            Interlocked.Read(ref _builtCount),
            Interlocked.Read(ref _takenCount),
            Interlocked.Read(ref _missCount),
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _restoreMillisecondsBits)),
            Interlocked.Read(ref _buildMillisecondsMicroseconds) / 1000d,
            Interlocked.Read(ref _compositionMillisecondsMicroseconds) / 1000d,
            Interlocked.Read(ref _rasterPreparationMillisecondsMicroseconds) / 1000d,
            Interlocked.Read(ref _renderPlanMillisecondsMicroseconds) / 1000d,
            Interlocked.Read(ref _commandPreparationMillisecondsMicroseconds) / 1000d,
            BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastBuildMillisecondsBits)),
            Volatile.Read(ref _lastBuiltFrame),
            Interlocked.Read(ref _droppedCount),
            Interlocked.Read(ref _prefetchedRasterFrameCount),
            Interlocked.Read(ref _prefetchedRasterMillisecondsMicroseconds) / 1000d);

        public void NotifyCurrentFrame(int frame)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var current = Math.Clamp(frame, _startFrame, _endFrame);
            Volatile.Write(ref _currentFrame, current);
            Volatile.Write(ref _requestedFrame, current);
            PruneStaleFrames(current);
            PrunePendingFrames(current);
            _wake.Set();
        }

        public void RequestFrame(int frame)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var requested = Math.Clamp(frame, _startFrame, _endFrame);
            Volatile.Write(ref _requestedFrame, requested);
            PruneStaleFrames(requested, requested);
            PrunePendingFrames(requested);
            _wake.Set();
        }

        public bool TryTake(
            int frame,
            bool allowForwardDrop,
            out PreparedPlaybackComposition prepared,
            out int takenFrame)
        {
            takenFrame = frame;
            if (_ready.TryRemove(frame, out prepared!))
            {
                // The initial frame is prepared once to avoid a cold first
                // paint. Once any frame has been consumed, the rolling window
                // should only prepare future frames; otherwise the current
                // frame would be queued again after every successful take.
                Interlocked.Exchange(ref _primeCurrentFrame, 0);
                Interlocked.Increment(ref _takenCount);
                _wake.Set();
                return true;
            }

            if (allowForwardDrop)
            {
                var span = _endFrame - _startFrame + 1;
                var bestFrame = -1;
                var bestDistance = int.MaxValue;
                foreach (var candidate in _ready.Keys)
                {
                    var distance = _loop
                        ? PositiveModulo((long)candidate - frame, span)
                        : candidate - frame;
                    if (distance <= 0
                        || distance > PlaybackCompositionPrefetchHorizon
                        || distance >= bestDistance)
                    {
                        continue;
                    }

                    bestFrame = candidate;
                    bestDistance = distance;
                }

                if (bestFrame >= 0 && _ready.TryRemove(bestFrame, out prepared!))
                {
                    Interlocked.Exchange(ref _primeCurrentFrame, 0);
                    takenFrame = bestFrame;
                    Interlocked.Increment(ref _takenCount);
                    Interlocked.Increment(ref _droppedCount);
                    _wake.Set();
                    return true;
                }
            }

            Interlocked.Increment(ref _missCount);
            prepared = null!;
            return false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cancellation.Cancel();
            _pending.CompleteAdding();
            _wake.Set();
            DisposeReadyFrames();
            if (_workerCompletion.IsCompleted)
            {
                DisposeWorkerResources(_workerCompletion);
            }
            else
            {
                // Snapshot restoration and raster preparation do not all have
                // cancellation points. Never wait for them on the UI thread;
                // release handles after every worker has stopped.
                _ = _workerCompletion.ContinueWith(
                    task => DisposeWorkerResources(task),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default);
            }
        }

        private void DisposeWorkerResources(Task completion)
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
            _ = completion.Exception;
            DisposeReadyFrames();
            _pending.Dispose();
            _wake.Dispose();
            _cancellation.Dispose();
        }

        private void DisposeReadyFrames()
        {
            foreach (var pair in _ready)
            {
                if (_ready.TryRemove(pair.Key, out var prepared)) prepared.Dispose();
            }
        }

        private void RunComposition()
        {
            SetPlaybackWorkerPriority();
            try
            {
                var restoreStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                var project = VectorProject.RestoreRestartSnapshot(_snapshot);
                Interlocked.Exchange(
                    ref _restoreMillisecondsBits,
                    BitConverter.DoubleToInt64Bits(
                        System.Diagnostics.Stopwatch.GetElapsedTime(restoreStarted).TotalMilliseconds));
                var definition = project.Scenes.FirstOrDefault(scene =>
                    string.Equals(scene.Id, _sceneId, StringComparison.Ordinal));
                if (definition is null)
                {
                    MarkFailed();
                    return;
                }

                while (!_cancellation.IsCancellationRequested)
                {
                    if (_ready.Count >= _capacity)
                    {
                        WaitForWork();
                        continue;
                    }

                    var frame = FindNextMissingFrame();
                    if (frame < 0)
                    {
                        WaitForWork();
                        continue;
                    }

                    var queued = false;
                    try
                    {
                        var buildStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                        var destination = new VectorScene();
                        var composition = SceneCompositionBuilder.Build(
                            destination,
                            definition,
                            project.DrawingObjects,
                            frame,
                            _fps);
                        Interlocked.Add(
                            ref _compositionMillisecondsMicroseconds,
                            (long)Math.Round(
                                System.Diagnostics.Stopwatch.GetElapsedTime(buildStarted)
                                    .TotalMilliseconds
                                * 1000d));
                        if (_cancellation.IsCancellationRequested) return;
                        if (!IsFrameInRequestWindow(frame)) continue;
                        _pending.Add(
                            new PendingPlaybackComposition(
                                destination,
                                composition,
                                frame,
                                buildStarted),
                            _cancellation.Token);
                        queued = true;
                    }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (InvalidOperationException) when (_cancellation.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        MarkFailed();
                        AppLog.Warn($"Playback composition build skipped: {exception.Message}");
                        return;
                    }
                    finally
                    {
                        // The reservation remains active until the preparation
                        // stage publishes the completed frame.
                        if (!queued) _building.TryRemove(frame, out _);
                    }
                    _wake.Set();
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                MarkFailed();
                AppLog.Warn($"Playback composition prefetch stopped: {exception.Message}");
            }
        }

        private void RunRasterPreparation()
        {
            SetPlaybackWorkerPriority();
            try
            {
                using var preparationStage = new StageControl(new VectorScene());
                preparationStage.Size = new Size(
                    Math.Max(1, _preparationState.Width),
                    Math.Max(1, _preparationState.Height));

                foreach (var pending in _pending.GetConsumingEnumerable(_cancellation.Token))
                {
                    if (!IsFrameInRequestWindow(pending.Frame))
                    {
                        _building.TryRemove(pending.Frame, out _);
                        _wake.Set();
                        continue;
                    }

                    var preparationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    PreparedPlaybackComposition? prepared = null;
                    try
                    {
                        object? rasterPreparation;
                        using (var workerLimit = ParallelBatch.PushWorkerLimit(
                                   PlaybackRasterPreparationMaximumWorkers))
                        {
                            preparationStage.ConfigureReference3DPlaybackPreparation(
                                pending.Scene,
                                pending.Composition,
                                pending.Frame,
                                _preparationState);
                            rasterPreparation = Environment.GetEnvironmentVariable(
                                    "VECTOR_DISABLE_PLAYBACK_RASTER_PREPARATION") == "1"
                                ? null
                                : preparationStage.PrepareReference3DPlaybackRaster();
                        }
                        var rasterFrame = rasterPreparation is null
                            ? null
                            : preparationStage.PrepareReference3DPlaybackRasterFrame(
                                rasterPreparation);
                        prepared = new PreparedPlaybackComposition(
                            pending.Scene,
                            pending.Composition,
                            rasterPreparation,
                            rasterFrame);
                        Interlocked.Add(
                            ref _renderPlanMillisecondsMicroseconds,
                            (long)Math.Round(
                                (preparationStage.LastReference3DLayerItemsMilliseconds
                                    + preparationStage.LastReference3DIntersectionMilliseconds
                                    + preparationStage.LastReference3DSortMilliseconds
                                    + preparationStage.LastReference3DOpticsMilliseconds)
                                * 1000d));
                        Interlocked.Add(
                            ref _commandPreparationMillisecondsMicroseconds,
                            (long)Math.Round(
                                preparationStage.LastDirect2DReference3DCpuRasterPreparationMilliseconds
                                * 1000d));
                        if (_cancellation.IsCancellationRequested) return;
                        if (!IsFrameInRequestWindow(pending.Frame)) continue;
                        if (_ready.TryAdd(pending.Frame, prepared!))
                        {
                            if (rasterFrame is not null)
                            {
                                Interlocked.Increment(ref _prefetchedRasterFrameCount);
                                Interlocked.Add(
                                    ref _prefetchedRasterMillisecondsMicroseconds,
                                    (long)Math.Round(
                                        preparationStage.LastDirect2DReference3DPlaybackRasterMilliseconds
                                        * 1000d));
                            }
                            var elapsedMilliseconds = System.Diagnostics.Stopwatch
                                .GetElapsedTime(pending.BuildStartedAt)
                                .TotalMilliseconds;
                            Interlocked.Increment(ref _builtCount);
                            Interlocked.Add(
                                ref _buildMillisecondsMicroseconds,
                                (long)Math.Round(elapsedMilliseconds * 1000d));
                            Interlocked.Exchange(
                                ref _lastBuildMillisecondsBits,
                                BitConverter.DoubleToInt64Bits(elapsedMilliseconds));
                            Volatile.Write(ref _lastBuiltFrame, pending.Frame);
                            prepared = null;
                        }
                    }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        MarkFailed();
                        AppLog.Warn($"Playback raster preparation skipped: {exception.Message}");
                        return;
                    }
                    finally
                    {
                        prepared?.Dispose();
                        Interlocked.Add(
                            ref _rasterPreparationMillisecondsMicroseconds,
                            (long)Math.Round(
                                System.Diagnostics.Stopwatch.GetElapsedTime(preparationStarted)
                                    .TotalMilliseconds
                                * 1000d));
                        _building.TryRemove(pending.Frame, out _);
                        _wake.Set();
                    }
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                MarkFailed();
                AppLog.Warn($"Playback raster preparation stopped: {exception.Message}");
            }
        }

        private void MarkFailed()
        {
            if (Volatile.Read(ref _disposed) != 0
                || _cancellation.IsCancellationRequested)
            {
                return;
            }

            if (Interlocked.Exchange(ref _failed, 1) == 0)
            {
                try
                {
                    _cancellation.Cancel();
                    _pending.CompleteAdding();
                    _wake.Set();
                }
                catch (ObjectDisposedException)
                {
                    // Final cleanup can race a worker's failure report.
                }
            }
        }

        private int FindNextMissingFrame()
        {
            var span = _endFrame - _startFrame + 1;
            if (span <= 1) return -1;

            // Reserve one slot for the exact frame requested by the UI. This
            // is allowed to temporarily exceed the rolling prefetch capacity;
            // RequestFrame prunes obsolete entries immediately afterward.
            var requested = Math.Clamp(
                Volatile.Read(ref _requestedFrame),
                _startFrame,
                _endFrame);
            var current = Math.Clamp(
                Volatile.Read(ref _currentFrame),
                _startFrame,
                _endFrame);
            if ((requested != current
                    || Volatile.Read(ref _primeCurrentFrame) != 0)
                && !_ready.ContainsKey(requested)
                && _building.TryAdd(requested, 0))
            {
                return requested;
            }

            if (_ready.Count + _building.Count >= _capacity) return -1;

            var searchLimit = Math.Min(span - 1, PlaybackCompositionPrefetchHorizon);
            for (var offset = 1; offset <= searchLimit; offset++)
            {
                var candidate = _loop
                    ? _startFrame + PositiveModulo((long)current - _startFrame + offset, span)
                    : current + offset;
                if (!_loop && candidate > _endFrame) break;
                if (!_ready.ContainsKey(candidate)
                    && _building.TryAdd(candidate, 0))
                {
                    return candidate;
                }
            }

            return -1;
        }

        private void PruneStaleFrames(int current, int preserveFrame = -1)
        {
            var span = _endFrame - _startFrame + 1;
            foreach (var frame in _ready.Keys)
            {
                if (frame == preserveFrame) continue;
                var distance = _loop
                    ? PositiveModulo((long)frame - current, span)
                    : frame - current;
                if (distance <= 0 || distance > PlaybackCompositionPrefetchHorizon)
                {
                    if (_ready.TryRemove(frame, out var prepared)) prepared.Dispose();
                }
            }
        }

        private void PrunePendingFrames(int requested)
        {
            if (Volatile.Read(ref _disposed) != 0 || _pending.IsCompleted) return;

            var retained = new List<PendingPlaybackComposition>(_capacity);
            for (var index = 0; index < _capacity; index++)
            {
                if (!_pending.TryTake(out var pending)) break;
                if (IsFrameInRequestWindow(pending.Frame, requested))
                {
                    retained.Add(pending);
                }
                else
                {
                    _building.TryRemove(pending.Frame, out _);
                }
            }

            foreach (var pending in retained)
            {
                if (!_pending.TryAdd(pending))
                {
                    _building.TryRemove(pending.Frame, out _);
                }
            }
        }

        private bool IsFrameInRequestWindow(int frame)
        {
            return IsFrameInRequestWindow(
                frame,
                Math.Clamp(
                    Volatile.Read(ref _requestedFrame),
                    _startFrame,
                    _endFrame));
        }

        private bool IsFrameInRequestWindow(int frame, int requested)
        {
            if (frame == requested) return true;

            var span = _endFrame - _startFrame + 1;
            var distance = _loop
                ? PositiveModulo((long)frame - requested, span)
                : frame - requested;
            return distance > 0 && distance <= PlaybackCompositionPrefetchHorizon;
        }

        private bool HasPotentialWork()
        {
            if (_cancellation.IsCancellationRequested) return true;
            if (_ready.Count + _building.Count >= _capacity) return false;

            var requested = Math.Clamp(
                Volatile.Read(ref _requestedFrame),
                _startFrame,
                _endFrame);
            var current = Math.Clamp(
                Volatile.Read(ref _currentFrame),
                _startFrame,
                _endFrame);
            if ((requested != current
                    || Volatile.Read(ref _primeCurrentFrame) != 0)
                && !_ready.ContainsKey(requested)
                && !_building.ContainsKey(requested))
            {
                return true;
            }

            var span = _endFrame - _startFrame + 1;
            var searchLimit = Math.Min(span - 1, PlaybackCompositionPrefetchHorizon);
            for (var offset = 1; offset <= searchLimit; offset++)
            {
                var candidate = _loop
                    ? _startFrame + PositiveModulo((long)current - _startFrame + offset, span)
                    : current + offset;
                if (!_loop && candidate > _endFrame) break;
                if (!_ready.ContainsKey(candidate)
                    && !_building.ContainsKey(candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private void WaitForWork()
        {
            try
            {
                // Reset before checking work. Producers set the event after
                // publishing their state, so a signal cannot be erased after
                // Wait returns.
                _wake.Reset();
                if (HasPotentialWork()) return;
                _wake.Wait(PlaybackPreloaderWaitMilliseconds, _cancellation.Token);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
            {
            }
        }

        private static int PositiveModulo(long value, int modulus)
        {
            if (modulus <= 0) return 0;
            var result = value % modulus;
            return (int)(result < 0 ? result + modulus : result);
        }

        private static void SetPlaybackWorkerPriority()
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            }
            catch (ThreadStateException)
            {
                // A platform may reject priority changes for managed worker
                // threads; the worker remains fully functional in that case.
            }
        }
    }

    private sealed class PlaybackSchedulerState
    {
        private readonly MainForm _owner;
        private readonly Action _tickCore;

        public PlaybackSchedulerState(MainForm owner)
        {
            _owner = owner;
            _tickCore = RunTickCore;
            UiTickCallback = RunReservedTick;
        }

        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<bool> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public PlaybackUiTickGate TickGate { get; } = new();
        public Action UiTickCallback { get; }

        private void RunReservedTick()
        {
            TickGate.ExecuteReserved(_tickCore);
        }

        private void RunTickCore()
        {
            if (!ReferenceEquals(_owner._playbackSchedulerState, this) || !_owner._playing) return;
            _owner.Tick();
        }
    }

    private sealed record PendingPlaybackComposition(
        VectorScene Scene,
        SceneCompositionResult Composition,
        int Frame,
        long BuildStartedAt);

    private sealed class PreparedPlaybackComposition : IDisposable
    {
        private int _disposed;

        public PreparedPlaybackComposition(
            VectorScene scene,
            SceneCompositionResult composition,
            object? rasterPreparation,
            object? rasterFrame)
        {
            Scene = scene;
            Composition = composition;
            RasterPreparation = rasterPreparation;
            RasterFrame = rasterFrame;
        }

        public VectorScene Scene { get; }

        public SceneCompositionResult Composition { get; }

        public object? RasterPreparation { get; }

        public object? RasterFrame { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (RasterFrame is IDisposable disposable) disposable.Dispose();
        }
    }
}

internal sealed class PlaybackUiTickGate
{
    private int _reserved;

    internal bool IsReserved => Volatile.Read(ref _reserved) != 0;

    internal bool TryReserve()
    {
        return Interlocked.CompareExchange(ref _reserved, 1, 0) == 0;
    }

    internal void ExecuteReserved(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!IsReserved) throw new InvalidOperationException("No playback UI tick is reserved.");
        try
        {
            callback();
        }
        finally
        {
            CancelReservation();
        }
    }

    internal void CancelReservation()
    {
        Volatile.Write(ref _reserved, 0);
    }
}

internal readonly record struct PlaybackCompositionPreloadMetrics(
    int ReadyFrames,
    long BuiltFrames,
    long AppliedFrames,
    long MissedFrames,
    double SnapshotRestoreMilliseconds,
    double BuildMilliseconds,
    double CompositionMilliseconds,
    double RasterPreparationMilliseconds,
    double RenderPlanMilliseconds,
    double CommandPreparationMilliseconds,
    double LastBuildMilliseconds,
    int LastBuiltFrame,
    long DroppedFrames,
    long PrefetchedRasterFrames,
    double PrefetchedRasterMilliseconds);
