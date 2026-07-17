namespace VectorAnimationEngine;

internal sealed class HotReloadCoordinator : IDisposable
{
    private const int CoalesceMilliseconds = 120;
    private const int FollowUpMilliseconds = 30;
    private const int DispatchLeaseMilliseconds = 2_000;
    private readonly object _sync = new();
    private readonly Func<Control?> _uiTarget;
    private readonly Action<HotReloadBatch> _apply;
    private readonly System.Threading.Timer _timer;
    private readonly int _coalesceMilliseconds;
    private HotReloadPlan? _pendingPlan;
    private DateTime _pendingSinceUtc;
    private long _latestGeneration;
    private long _dispatchSequence;
    private bool _dispatchPending;
    private bool _applyStarted;
    private DateTime _dispatchPostedUtc;
    private HotReloadBatch? _inFlightBatch;
    private bool _disposed;

    public HotReloadCoordinator(
        Func<Control?> uiTarget,
        Action<HotReloadBatch> apply,
        int coalesceMilliseconds = CoalesceMilliseconds)
    {
        _uiTarget = uiTarget ?? throw new ArgumentNullException(nameof(uiTarget));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _coalesceMilliseconds = Math.Clamp(coalesceMilliseconds, 1, 2_000);
        _timer = new System.Threading.Timer(DispatchPending, null, Timeout.Infinite, Timeout.Infinite);
    }

    public long Enqueue(HotReloadPlan plan)
    {
        if (plan.Modules == HotReloadModule.None) return Volatile.Read(ref _latestGeneration);
        lock (_sync)
        {
            if (_disposed) return _latestGeneration;
            _latestGeneration++;
            if (_pendingPlan is null) _pendingSinceUtc = DateTime.UtcNow;
            _pendingPlan = _pendingPlan is { } pending ? pending.Merge(plan) : plan;
            _timer.Change(_coalesceMilliseconds, Timeout.Infinite);
            AppLog.Info($"Queued hot reload generation {_latestGeneration}: {_pendingPlan.Value.Modules}; types: {_pendingPlan.Value.UpdatedTypes}");
            return _latestGeneration;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _pendingPlan = null;
            _inFlightBatch = null;
            _dispatchSequence++;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        _timer.Dispose();
    }

    private void DispatchPending(object? state)
    {
        HotReloadBatch batch;
        long dispatchSequence;
        lock (_sync)
        {
            if (_disposed) return;
            if (_dispatchPending)
            {
                if (_applyStarted) return;
                var elapsed = DateTime.UtcNow - _dispatchPostedUtc;
                if (elapsed < TimeSpan.FromMilliseconds(DispatchLeaseMilliseconds))
                {
                    var remaining = Math.Max(1, DispatchLeaseMilliseconds - (int)elapsed.TotalMilliseconds);
                    _timer.Change(remaining, Timeout.Infinite);
                    return;
                }

                if (_inFlightBatch is { } expired)
                {
                    _pendingPlan = _pendingPlan is { } pending ? expired.Plan.Merge(pending) : expired.Plan;
                    _pendingSinceUtc = expired.QueuedUtc;
                }
                _inFlightBatch = null;
                _dispatchPending = false;
                _dispatchSequence++;
                _timer.Change(_coalesceMilliseconds, Timeout.Infinite);
                AppLog.Warn("A posted hot reload did not reach the UI thread before its lease expired; the batch was requeued.");
                return;
            }
            if (_pendingPlan is not { } plan) return;
            batch = new HotReloadBatch(_latestGeneration, plan, _pendingSinceUtc);
            _pendingPlan = null;
            _dispatchPending = true;
            _applyStarted = false;
            _dispatchPostedUtc = DateTime.UtcNow;
            _inFlightBatch = batch;
            dispatchSequence = ++_dispatchSequence;
            _timer.Change(DispatchLeaseMilliseconds, Timeout.Infinite);
        }

        var target = _uiTarget();
        if (target is null || target.IsDisposed || !target.IsHandleCreated)
        {
            Requeue(batch, "The UI target was unavailable while dispatching a hot reload.");
            return;
        }

        try
        {
            target.BeginInvoke(() => ApplyOnUiThread(batch, dispatchSequence));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Requeue(batch, "The UI target changed while dispatching a hot reload.", ex);
        }
    }

    private void ApplyOnUiThread(HotReloadBatch batch, long dispatchSequence)
    {
        lock (_sync)
        {
            if (_disposed || !_dispatchPending || _dispatchSequence != dispatchSequence) return;
            _applyStarted = true;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        try
        {
            _apply(batch);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Unhandled hot reload coordinator failure for generation {batch.Generation}", ex);
        }
        finally
        {
            lock (_sync)
            {
                if (_dispatchSequence == dispatchSequence)
                {
                    _dispatchPending = false;
                    _applyStarted = false;
                    _inFlightBatch = null;
                    if (!_disposed && _pendingPlan is not null) _timer.Change(FollowUpMilliseconds, Timeout.Infinite);
                }
            }
        }
    }

    private void Requeue(HotReloadBatch batch, string message, Exception? exception = null)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _pendingPlan = _pendingPlan is { } pending ? batch.Plan.Merge(pending) : batch.Plan;
            _pendingSinceUtc = batch.QueuedUtc;
            _latestGeneration = Math.Max(_latestGeneration, batch.Generation);
            _dispatchPending = false;
            _applyStarted = false;
            _inFlightBatch = null;
            _dispatchSequence++;
            _timer.Change(_coalesceMilliseconds, Timeout.Infinite);
        }

        if (exception is null) AppLog.Warn(message);
        else AppLog.Error(message, exception);
    }
}
