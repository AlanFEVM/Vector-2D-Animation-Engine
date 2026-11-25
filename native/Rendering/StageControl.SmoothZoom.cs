using System.Diagnostics;

namespace VectorAnimationEngine;

/// <summary>
/// 平面视图滚轮缩放的平滑层。
/// 滚轮是离散输入：每个刻度只产生一次事件，直接按事件改视图会让画面"一格一跳"，
/// 快速滚动时 WM_PAINT 还会被后续滚轮消息挤掉，表现为缩放与鼠标刻度同步的顿挫。
/// 这里把离散输入累积成目标缩放，再由独立的高刷新率帧循环逼近目标：
/// 帧循环一次只投递一个回调，因此不会抢占指针输入，也不需要等下一次滚轮消息。
/// </summary>
internal sealed partial class StageControl
{
    /// <summary>一整格滚轮（120）对应的缩放倍率。提高灵敏度即提高该值。</summary>
    internal const float WheelZoomStepBase = 1.25f;

    /// <summary>平面视图缩放下限，与 <see cref="ApplyZoomAt"/> 的钳制保持一致。</summary>
    internal const float MinimumZoom = 0.02f;

    /// <summary>平面视图缩放上限，与 <see cref="ApplyZoomAt"/> 的钳制保持一致。</summary>
    internal const float MaximumZoom = 64f;

    // 40 ms 时间常数：单格滚轮约 150 ms 走完 97% 的剩余距离，既能跟住连续滚轮，又消除了单格跳变。
    private const double SmoothZoomTimeConstantMilliseconds = 40;

    // 剩余对数距离低于 0.1% 时直接落到目标，避免指数尾部无限逼近导致帧循环停不下来。
    private const double SmoothZoomSettleLogTolerance = 0.001;

    // 144 Hz 目标帧距；实际呈现仍受 Direct2D 呈现与合成器限制。
    private static readonly long SmoothZoomFrameInterval = Math.Max(1, Stopwatch.Frequency / 144);

    private bool _smoothZoomActive;
    private float _smoothZoomTarget = 1;
    private Point _smoothZoomAnchor;
    private bool _smoothZoomInteractivePreview = true;
    private long _smoothZoomLastTick;
    private System.Threading.Timer? _smoothZoomFrameTimer;
    private int _smoothZoomFramePosted;
    private long _smoothZoomNextFrameAt;
    private bool _smoothZoomTimerResolution;

    /// <summary>平滑缩放会话是否仍在逼近目标（回归与状态查询使用）。</summary>
    internal bool SmoothZoomActive => _smoothZoomActive;

    /// <summary>
    /// 滚轮缩放：把离散滚轮输入累积成目标缩放，并按帧率平滑逼近。
    /// 返回时视图已经推进了第一小步，因此手感与原来的即时缩放一致，但不会再出现整格跳变。
    /// </summary>
    public void ZoomAtSmooth(Point screen, float factor, bool interactivePreview = true)
    {
        if (!float.IsFinite(factor) || factor <= 0) return;
        if (!IsHandleCreated || IsDisposed || Disposing || _disposingResources)
        {
            // 没有可呈现的窗口句柄时没有帧循环可用，退回即时缩放，避免输入被静默丢弃。
            ZoomAt(screen, factor, interactivePreview);
            return;
        }

        var baseZoom = _smoothZoomActive ? _smoothZoomTarget : Zoom;
        var target = (float)Math.Clamp((double)baseZoom * factor, MinimumZoom, MaximumZoom);
        _smoothZoomAnchor = screen;
        _smoothZoomInteractivePreview = interactivePreview;
        if (!_smoothZoomActive)
        {
            // 已在缩放上下限：视图不会变化，不必开启帧循环。
            if (target == Zoom) return;
            _smoothZoomActive = true;
            _smoothZoomTarget = target;
            _smoothZoomLastTick = Stopwatch.GetTimestamp() - SmoothZoomFrameInterval;
            StartSmoothZoomFrames();
        }
        else if (target == _smoothZoomTarget)
        {
            return;
        }
        else
        {
            _smoothZoomTarget = target;
        }

        AdvanceSmoothZoomFrame(Stopwatch.GetTimestamp());
    }

    /// <summary>取消平滑缩放并保留当前画面（程序化缩放与视图替换时调用）。</summary>
    private void CancelSmoothZoom()
    {
        if (_smoothZoomFrameTimer is null && !_smoothZoomActive) return;
        StopSmoothZoomFrames();
        _smoothZoomActive = false;
        _smoothZoomTarget = Zoom;
    }

    private void AdvanceSmoothZoomFrame(long now)
    {
        if (!_smoothZoomActive) return;
        var elapsed = Stopwatch.GetElapsedTime(_smoothZoomLastTick, now).TotalMilliseconds;
        _smoothZoomLastTick = now;
        if (elapsed <= 0) elapsed = 1000d / 144d;
        else if (elapsed > 250) elapsed = 250;

        var current = Zoom;
        var target = _smoothZoomTarget;
        if (!(current > 0) || !float.IsFinite(target) || target <= 0)
        {
            CancelSmoothZoom();
            return;
        }

        // 在对数空间插值：任意缩放级别下每帧的视觉推进量一致，不会在低缩放下发飘、高缩放下发黏。
        var remaining = Math.Log(target / (double)current);
        if (Math.Abs(remaining) <= SmoothZoomSettleLogTolerance)
        {
            StopSmoothZoomFrames();
            _smoothZoomActive = false;
            _smoothZoomTarget = Zoom;
            ApplyZoomAt(_smoothZoomAnchor, target, _smoothZoomInteractivePreview);
            return;
        }

        var progress = 1d - Math.Exp(-elapsed / SmoothZoomTimeConstantMilliseconds);
        var next = (float)(target * Math.Exp(-remaining * (1d - progress)));
        ApplyZoomAt(_smoothZoomAnchor, next, _smoothZoomInteractivePreview);
    }

    private void StartSmoothZoomFrames()
    {
        if (_smoothZoomFrameTimer is not null) return;
        // 1 ms 定时器精度：默认 15.6 ms 的粒度会把 144 Hz 目标压回 60 Hz。
        if (!_smoothZoomTimerResolution) _smoothZoomTimerResolution = BeginSceneRefreshTimerResolution(1) == 0;
        Volatile.Write(ref _smoothZoomNextFrameAt, Stopwatch.GetTimestamp());
        _smoothZoomFrameTimer = new System.Threading.Timer(_ =>
        {
            var now = Stopwatch.GetTimestamp();
            if (now < Volatile.Read(ref _smoothZoomNextFrameAt)
                || Interlocked.CompareExchange(ref _smoothZoomFramePosted, 1, 0) != 0) return;
            Volatile.Write(ref _smoothZoomNextFrameAt, now + SmoothZoomFrameInterval);
            try { BeginInvoke((Action)PresentSmoothZoomFrame); }
            catch (InvalidOperationException) { Interlocked.Exchange(ref _smoothZoomFramePosted, 0); }
        }, null, 1, 1);
    }

    private void PresentSmoothZoomFrame()
    {
        try
        {
            if (_smoothZoomFrameTimer is null || _disposingResources || IsDisposed || Disposing
                || !IsHandleCreated || !Visible || !_smoothZoomActive)
            {
                CancelSmoothZoom();
                return;
            }

            AdvanceSmoothZoomFrame(Stopwatch.GetTimestamp());
            // 平滑帧由定时器驱动而不是 WM_PAINT，必须同步呈现：否则下一次滚轮消息会把这一帧挤掉。
            if (!_paintInProgress) Update();
        }
        finally { Interlocked.Exchange(ref _smoothZoomFramePosted, 0); }
    }

    private void StopSmoothZoomFrames()
    {
        Interlocked.Exchange(ref _smoothZoomFrameTimer, null)?.Dispose();
        Interlocked.Exchange(ref _smoothZoomFramePosted, 0);
        if (!_smoothZoomTimerResolution) return;
        _smoothZoomTimerResolution = false;
        EndSceneRefreshTimerResolution(1);
    }
}
