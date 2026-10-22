using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace VectorAnimationEngine;

internal sealed partial class MainForm
{
    private ShotPreviewDisplayClock? _shotPreviewDisplayClock;
    private ShotPreviewFrameKey? _shotPreviewFrameKey;

    private readonly record struct ShotPreviewFrameKey(
        ShotPreviewSourceState Source, SceneShotSettings Settings, Size Size, string Caption);

    private void UpdateShotPreviewScheduler()
    {
        if (!_shotDirectorInitialized || IsDisposed || Disposing || !IsHandleCreated
            || !Visible || WindowState == FormWindowState.Minimized || !_shotPreviewOverlay.Visible)
        {
            StopShotPreviewScheduler();
            return;
        }

        var display = Screen.FromControl(_shotPreviewOverlay).DeviceName;
        _shotPreviewDisplayClock ??= new ShotPreviewDisplayClock(this, display);
        _shotPreviewDisplayClock.DisplayName = display;
    }

    private void StopShotPreviewScheduler()
    {
        _shotPreviewDisplayClock?.Dispose();
        _shotPreviewDisplayClock = null;
        _shotPreviewFrameKey = null;
        _stage.ReleaseShotPreviewResources();
        if (!_shotPreviewOverlay.IsDisposed) _shotPreviewOverlay.SetPreview(null);
    }

    // The worker only waits on the selected output and posts a bounded UI callback. Scene, GDI and
    // shared SVG resources are always read on the UI thread, after the current edit/frame handoff.
    private sealed class ShotPreviewDisplayClock : IDisposable
    {
        private readonly MainForm _owner;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly PlaybackUiTickGate _gate = new();
        private readonly Action _callback;
        private readonly Task _worker;
        private string _displayName;
        private int _stopped;
        private bool _captureFailureLogged;

        public ShotPreviewDisplayClock(MainForm owner, string displayName)
        {
            _owner = owner;
            _displayName = displayName;
            _callback = () => _gate.ExecuteReserved(Tick);
            _worker = Task.Factory.StartNew(Run, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public string DisplayName
        {
            get => Volatile.Read(ref _displayName);
            set => Volatile.Write(ref _displayName, value);
        }

        private void Tick()
        {
            if (Volatile.Read(ref _stopped) != 0 || !ReferenceEquals(_owner._shotPreviewDisplayClock, this)) return;
            _owner.UpdateShotPreviewScheduler();
            if (!ReferenceEquals(_owner._shotPreviewDisplayClock, this)) return;
            try
            {
                _owner.RenderShotPreviewFrame();
                _captureFailureLogged = false;
            }
            catch (Exception ex)
            {
                if (!_captureFailureLogged) AppLog.Error("Shot preview capture failed", ex);
                _captureFailureLogged = true;
                _owner._shotPreviewFrameKey = null;
                _owner._stage.ReleaseShotPreviewResources();
                _owner._shotPreviewOverlay.SetPreview(null);
            }
        }

        private void Run()
        {
            IDXGIOutput? output = null;
            var boundDisplay = "";
            var retryAt = 0L;
            var fallbackMilliseconds = 1000d / 60d;
            var nextFallbackAt = Stopwatch.GetTimestamp();
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var display = DisplayName;
                    if (display != boundDisplay || output is null && Stopwatch.GetTimestamp() >= retryAt)
                    {
                        output?.Dispose();
                        output = null;
                        boundDisplay = display;
                        try { output = FindOutput(display); }
                        catch (Exception ex) { AppLog.Warn($"Shot preview display clock unavailable: {ex.Message}"); }
                        fallbackMilliseconds = GetFallbackInterval(display);
                        retryAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
                    }

                    if (output is not null)
                    {
                        try { output.WaitForVBlank(); }
                        catch (Exception ex)
                        {
                            AppLog.Warn($"Shot preview display wait failed: {ex.Message}");
                            output.Dispose();
                            output = null;
                            retryAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
                            nextFallbackAt = Stopwatch.GetTimestamp();
                            continue;
                        }
                    }
                    else
                    {
                        var now = Stopwatch.GetTimestamp();
                        var interval = (long)(Stopwatch.Frequency * fallbackMilliseconds / 1000d);
                        nextFallbackAt += interval;
                        if (nextFallbackAt <= now) nextFallbackAt = now + interval;
                        var wait = Math.Max(1, (int)Math.Ceiling(
                            (nextFallbackAt - now) * 1000d / Stopwatch.Frequency));
                        if (_cancellation.Token.WaitHandle.WaitOne(wait)) break;
                    }

                    if (_cancellation.IsCancellationRequested || !_gate.TryReserve()) continue;
                    try { _owner.BeginInvoke(_callback); }
                    catch (InvalidOperationException) { _gate.CancelReservation(); }
                }
            }
            finally { output?.Dispose(); }
        }

        private static IDXGIOutput? FindOutput(string display)
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
            {
                using (adapter)
                {
                    for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                    {
                        try
                        {
                            if (output.Description.AttachedToDesktop && output.Description.DeviceName == display)
                                return output;
                        }
                        catch { output.Dispose(); throw; }
                        output.Dispose();
                    }
                }
            }
            return null;
        }

        private static double GetFallbackInterval(string display)
        {
            var dc = CreateDC("DISPLAY", display, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) return 1000d / 60d;
            try
            {
                var hertz = GetDeviceCaps(dc, 116); // VREFRESH; 0/1 mean the default/unknown rate.
                return 1000d / (hertz > 1 ? hertz : 60);
            }
            finally { DeleteDC(dc); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            _cancellation.Cancel();
            _ = _worker.ContinueWith(_ => _cancellation.Dispose(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr initData);
        [DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr dc, int index);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr dc);
    }
}
