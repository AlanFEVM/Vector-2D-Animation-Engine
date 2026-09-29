using System.Drawing;
using System.Numerics;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;

namespace VectorAnimationEngine;

internal sealed class StageGpuDevice : IDisposable
{
    private static readonly D3DFeatureLevel[] FeatureLevels =
    [
        D3DFeatureLevel.Level_11_0,
        D3DFeatureLevel.Level_10_1,
        D3DFeatureLevel.Level_10_0,
        D3DFeatureLevel.Level_9_3
    ];

    private readonly ID2D1Device _d2dDevice;
    private readonly IDXGISwapChain1 _swapChain;
    private IDXGISurface? _backBuffer;
    private ID2D1Bitmap1? _targetBitmap;
    private bool _disposed;

    private StageGpuDevice(
        ID3D11Device device,
        ID3D11DeviceContext immediateContext,
        ID2D1Device d2dDevice,
        ID2D1DeviceContext context,
        IDXGISwapChain1 swapChain,
        IDXGISurface backBuffer,
        ID2D1Bitmap1 targetBitmap,
        string adapterName)
    {
        Device = device;
        ImmediateContext = immediateContext;
        _d2dDevice = d2dDevice;
        Context = context;
        _swapChain = swapChain;
        _backBuffer = backBuffer;
        _targetBitmap = targetBitmap;
        AdapterName = adapterName;
    }

    public ID2D1DeviceContext Context { get; }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext ImmediateContext { get; }

    public ID2D1Bitmap1? TargetBitmap => _targetBitmap;

    public string AdapterName { get; }

    public static StageGpuDevice? TryCreate(
        IntPtr hwnd,
        Size size,
        ID2D1Factory1 factory)
    {
        if (Environment.GetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU") == "1") return null;
        if (hwnd == IntPtr.Zero)
        {
            var failure = new ArgumentException("The stage window handle is null.", nameof(hwnd));
            AppLog.Error("Direct2D GPU device creation failed; the stage window handle is invalid.", failure);
            return null;
        }

        IDXGIFactory2? dxgiFactory = null;
        Exception? lastFailure = null;
        try
        {
            dxgiFactory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            if (dxgiFactory is null)
                throw new InvalidOperationException("DXGI factory creation returned null.");

            IDXGIFactory6? preferenceFactory = null;
            try
            {
                preferenceFactory = dxgiFactory.QueryInterface<IDXGIFactory6>();
                var adapterCount = preferenceFactory.GetAdapterByGpuPreference(GpuPreference.HighPerformance);
                for (var index = 0; index < adapterCount; index++)
                {
                    IDXGIAdapter1? adapter = null;
                    try
                    {
                        var enumeration = preferenceFactory.EnumAdapterByGpuPreference(
                            (uint)index,
                            GpuPreference.HighPerformance,
                            out adapter);
                        if (enumeration.Failure || adapter is null) continue;
                        if (IsSoftwareAdapter(adapter)) continue;

                        var candidate = TryCreateForAdapter(
                            hwnd,
                            size,
                            factory,
                            dxgiFactory,
                            adapter,
                            out var failure);
                        if (candidate is not null)
                        {
                            AppLog.Info(
                                $"Direct2D GPU device initialized on adapter '{candidate.AdapterName}' with immediate presentation.");
                            return candidate;
                        }
                        lastFailure = failure;
                    }
                    catch (Exception ex)
                    {
                        lastFailure = ex;
                    }
                    finally
                    {
                        adapter?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                lastFailure = ex;
            }
            finally
            {
                preferenceFactory?.Dispose();
            }

            // DXGI 1.6 is unavailable on some supported systems. The base
            // enumeration still gives us hardware adapters for a safe fallback.
            for (uint index = 0; ; index++)
            {
                IDXGIAdapter1? adapter = null;
                try
                {
                    var enumeration = dxgiFactory.EnumAdapters1(index, out adapter);
                    if (enumeration.Failure || adapter is null) break;
                    if (IsSoftwareAdapter(adapter)) continue;

                    var candidate = TryCreateForAdapter(
                        hwnd,
                        size,
                        factory,
                        dxgiFactory,
                        adapter,
                        out var failure);
                    if (candidate is not null)
                    {
                        AppLog.Info(
                            $"Direct2D GPU device initialized on adapter '{candidate.AdapterName}' with immediate presentation.");
                        return candidate;
                    }
                    lastFailure = failure;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                    break;
                }
                finally
                {
                    adapter?.Dispose();
                }
            }

            lastFailure ??= new InvalidOperationException("No usable hardware adapter was found.");
        }
        catch (Exception ex)
        {
            lastFailure = ex;
        }
        finally
        {
            dxgiFactory?.Dispose();
        }

        AppLog.Error("Direct2D GPU device creation failed; falling back to the legacy render target.", lastFailure);
        return null;
    }

    public void Resize(Size size)
    {
        ThrowIfDisposed();

        var width = Math.Max(1, size.Width);
        var height = Math.Max(1, size.Height);
        if (_targetBitmap is not null
            && _targetBitmap.PixelSize.Width == width
            && _targetBitmap.PixelSize.Height == height)
        {
            return;
        }

        UnbindAndDisposeTarget();
        FlushImmediateContext();
        _swapChain.ResizeBuffers(
            0,
            (uint)width,
            (uint)height,
            Format.Unknown,
            SwapChainFlags.None).CheckError();
        CreateTargetBitmap();
    }

    public void Present()
    {
        ThrowIfDisposed();
        _swapChain.Present(0, PresentFlags.None).CheckError();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            Context.Target = null;
        }
        catch
        {
            // Continue releasing all native resources after a device failure.
        }

        DisposeQuietly(_targetBitmap);
        _targetBitmap = null;
        DisposeQuietly(_backBuffer);
        _backBuffer = null;
        FlushImmediateContextQuietly();
        DisposeQuietly(Context);
        DisposeQuietly(_d2dDevice);
        DisposeQuietly(_swapChain);
        DisposeQuietly(ImmediateContext);
        DisposeQuietly(Device);
    }

    private static StageGpuDevice? TryCreateForAdapter(
        IntPtr hwnd,
        Size size,
        ID2D1Factory1 factory,
        IDXGIFactory2 dxgiFactory,
        IDXGIAdapter1 adapter,
        out Exception? failure)
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? immediateContext = null;
        ID2D1Device? d2dDevice = null;
        ID2D1DeviceContext? context = null;
        IDXGISwapChain1? swapChain = null;
        IDXGISurface? backBuffer = null;
        ID2D1Bitmap1? targetBitmap = null;
        failure = null;

        try
        {
            var deviceResult = D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                FeatureLevels,
                out device,
                out _,
                out immediateContext);
            deviceResult.CheckError();
            if (device is null || immediateContext is null)
                throw new InvalidOperationException("D3D11 device creation returned no device or context.");

            using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
            {
                d2dDevice = factory.CreateDevice(dxgiDevice);
            }

            context = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
            var width = Math.Max(1, size.Width);
            var height = Math.Max(1, size.Height);
            var description = new SwapChainDescription1(
                (uint)width,
                (uint)height,
                Format.B8G8R8A8_UNorm,
                false,
                Usage.RenderTargetOutput,
                2,
                Scaling.Stretch,
                SwapEffect.FlipDiscard,
                Vortice.DXGI.AlphaMode.Ignore,
                SwapChainFlags.None);
            swapChain = dxgiFactory.CreateSwapChainForHwnd(
                device,
                hwnd,
                description,
                null,
                null!);
            backBuffer = swapChain.GetBuffer<IDXGISurface>(0);
            var properties = new BitmapProperties1(
                new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
                96,
                96,
                BitmapOptions.Target | BitmapOptions.CannotDraw);
            targetBitmap = context.CreateBitmapFromDxgiSurface(backBuffer, properties);
            context.Target = targetBitmap;
            context.SetDpi(96, 96);
            context.Transform = Matrix3x2.Identity;

            var adapterName = adapter.Description1.Description;
            if (string.IsNullOrWhiteSpace(adapterName)) adapterName = "Unknown hardware adapter";
            return new StageGpuDevice(
                device,
                immediateContext,
                d2dDevice,
                context,
                swapChain,
                backBuffer,
                targetBitmap,
                adapterName);
        }
        catch (Exception ex)
        {
            failure = ex;
            try
            {
                if (context is not null) context.Target = null;
            }
            catch
            {
                // The context may already be lost; continue cleanup.
            }

            DisposeQuietly(targetBitmap);
            DisposeQuietly(backBuffer);
            DisposeQuietly(context);
            DisposeQuietly(d2dDevice);
            DisposeQuietly(swapChain);
            DisposeQuietly(immediateContext);
            DisposeQuietly(device);
            return null;
        }
    }

    private void CreateTargetBitmap()
    {
        _backBuffer = _swapChain.GetBuffer<IDXGISurface>(0);
        try
        {
            var properties = new BitmapProperties1(
                new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied),
                96,
                96,
                BitmapOptions.Target | BitmapOptions.CannotDraw);
            _targetBitmap = Context.CreateBitmapFromDxgiSurface(_backBuffer, properties);
            Context.Target = _targetBitmap;
            Context.SetDpi(96, 96);
            Context.Transform = Matrix3x2.Identity;
        }
        catch
        {
            UnbindAndDisposeTarget();
            throw;
        }
    }

    private void UnbindAndDisposeTarget()
    {
        try
        {
            Context.Target = null;
        }
        finally
        {
            DisposeQuietly(_targetBitmap);
            _targetBitmap = null;
            DisposeQuietly(_backBuffer);
            _backBuffer = null;
        }
    }

    private void FlushImmediateContext()
    {
        ImmediateContext.ClearState();
        ImmediateContext.Flush();
    }

    private void FlushImmediateContextQuietly()
    {
        try
        {
            FlushImmediateContext();
        }
        catch
        {
            // Device removal can make the final D3D flush fail.
        }
    }

    private static bool IsSoftwareAdapter(IDXGIAdapter1 adapter)
    {
        return adapter.Description1.Flags.HasFlag(AdapterFlags.Software);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StageGpuDevice));
    }

    private static void DisposeQuietly(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch
        {
            // Native teardown is best effort after device removal.
        }
    }
}
