using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VectorAnimationEngine;

internal sealed partial class StageControl
{
    internal bool ContinuousSceneRefreshEnabled { get; set; }
    private System.Threading.Timer? _sceneRefreshTimer;
    private long _sceneRefreshDeadline;
    private int _sceneRefreshPosted;
    private long _sceneRefreshPostedPresentation;
    private bool _presentingSceneRefresh;
    private bool _sceneRefreshResolution;
    // Leave delivery headroom above the 144 Hz presentation requirement: a
    // Windows message arriving slightly late must not lower the sustained rate.
    private static readonly long SceneRefreshInterval = Math.Max(1, Stopwatch.Frequency / 240);

    private void UpdateSceneRefreshScheduling()
    {
        if (!ContinuousSceneRefreshEnabled || !Visible || !IsHandleCreated || _disposingResources
            || !RendersReferenceProjection || PlaybackActive)
        {
            StopSceneRefreshScheduling();
            return;
        }
        if (_sceneRefreshTimer is not null) return;
        _sceneRefreshResolution = BeginSceneRefreshTimerResolution(1) == 0;
        _sceneRefreshDeadline = Stopwatch.GetTimestamp() + SceneRefreshInterval;
        _sceneRefreshTimer = new System.Threading.Timer(_ =>
        {
            var now = Stopwatch.GetTimestamp();
            if (now < Volatile.Read(ref _sceneRefreshDeadline)
                || Interlocked.CompareExchange(ref _sceneRefreshPosted, 1, 0) != 0) return;
            // At most one UI callback is queued. Missing a deadline never adds
            // catch-up renders ahead of mouse/keyboard input.
            var next = Volatile.Read(ref _sceneRefreshDeadline) + SceneRefreshInterval;
            Interlocked.Exchange(ref _sceneRefreshDeadline, next > now ? next : now + SceneRefreshInterval);
            _sceneRefreshPostedPresentation = Volatile.Read(ref _lastFramePresentedAt);
            try { BeginInvoke((Action)PresentSceneRefresh); }
            catch (InvalidOperationException) { Interlocked.Exchange(ref _sceneRefreshPosted, 0); }
        }, null, 1, 1);
    }

    private void PresentSceneRefresh()
    {
        try
        {
            if (_sceneRefreshTimer is null || _disposingResources || !Visible || !IsHandleCreated) return;
            if (FindForm() is { } form && (!form.Visible || form.WindowState == FormWindowState.Minimized)) return;
            if (!RendersReferenceProjection || PlaybackActive) { StopSceneRefreshScheduling(); return; }
            // Mouse input may have presented a newer frame since this callback
            // was queued. That presentation already satisfies the refresh.
            if (_lastFramePresentedAt != _sceneRefreshPostedPresentation) return;
            InvalidateOverlay();
            _presentingSceneRefresh = true;
            try { Update(); }
            finally { _presentingSceneRefresh = false; }
        }
        finally { Interlocked.Exchange(ref _sceneRefreshPosted, 0); }
    }

    private void StopSceneRefreshScheduling()
    {
        Interlocked.Exchange(ref _sceneRefreshTimer, null)?.Dispose();
        if (_sceneRefreshResolution) EndSceneRefreshTimerResolution(1);
        _sceneRefreshResolution = false;
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint BeginSceneRefreshTimerResolution(uint milliseconds);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint EndSceneRefreshTimerResolution(uint milliseconds);
}
