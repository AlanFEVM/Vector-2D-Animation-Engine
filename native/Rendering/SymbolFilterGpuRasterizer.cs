using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace VectorAnimationEngine;

// Owned by one compositor, never shared between foreground/background renderers.
// The isolated group remains compatible with the existing mask/blend pipeline.
// Only its exact content ROI crosses the bus; all filter intermediates stay on GPU.
internal sealed class SymbolFilterGpuRasterizer : IDisposable
{
    private const int MaximumPixels = 4 * 1024 * 1024;
    private static readonly Lazy<byte[][]> Bytecode = new(() =>
        new[] { Compile(ShaderSource, "Pixels"), Compile(ShaderSource, "Lines") });
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11ComputeShader? _pixels, _lines;
    private ID3D11Buffer? _constants;
    private Texture? _input, _output;
    private readonly Texture?[] _work = new Texture?[3];
    private ID3D11Texture2D? _readback;
    private int _capacityWidth, _capacityHeight;
    private bool _failed, _disposed;
    // Exact, bounded LRU: alternating symbols must not evict each other every frame.
    private const int CacheByteBudget = 32 * 1024 * 1024;
    private readonly List<ResultEntry> _results = [];
    private sealed class ResultEntry(int length)
    {
        internal readonly byte[] Input = new byte[length], Output = new byte[length];
        internal int Width, Height;
        internal SymbolFilters Filters;
        internal float Scale;
    }
    internal long CompletedApplications { get; private set; }
    internal long CacheHits { get; private set; }
    internal string? AdapterName { get; private set; }
    internal string? LastFailure { get; private set; }

    internal bool TryApply(byte[] bytes, int width, int height, SymbolFilters filters, float scale)
    {
        // Tiny groups are cheaper on CPU; never call a software D3D driver an accelerator.
        if (_disposed || _failed || (long)width * height < 4096
            || width > 8192 || height > 8192 || (long)width * height > MaximumPixels
            || Environment.GetEnvironmentVariable("VECTOR_DISABLE_GPU_FILTERS") == "1"
            || Environment.GetEnvironmentVariable("VECTOR_DISABLE_EXPLICIT_GPU") == "1") return false;
        try
        {
            int byteCount = checked(width * height * 4);
            for (int i = 0; i < _results.Count; i++)
            {
                var cached = _results[i];
                if (cached.Width != width || cached.Height != height || cached.Filters != filters || cached.Scale != scale
                    || !bytes.AsSpan(0, byteCount).SequenceEqual(cached.Input)) continue;
                cached.Output.CopyTo(bytes, 0);
                _results.RemoveAt(i); _results.Add(cached);
                CacheHits++;
                return true;
            }
            EnsureDevice();
            EnsureSurfaces(width, height);
            ResultEntry? entry = null;
            if (byteCount * 2L <= CacheByteBudget)
            {
                long used = _results.Sum(item => (long)item.Input.Length * 2);
                while (_results.Count > 0 && (_results.Count >= 8 || used + byteCount * 2L > CacheByteBudget))
                {
                    var oldest = _results[0]; _results.RemoveAt(0);
                    used -= oldest.Input.Length * 2L;
                    if (oldest.Input.Length == byteCount) entry = oldest;
                }
                entry ??= new ResultEntry(byteCount);
                bytes.AsSpan(0, byteCount).CopyTo(entry.Input);
            }
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                _context!.UpdateSubresource(_input!.Resource, 0,
                    new Box(0, 0, 0, width, height, 1), pin.AddrOfPinnedObject(), (uint)(width * 4), 0);
            }
            finally { pin.Free(); }
            Texture current = _input!;
            if (filters.Blur.Enabled) current = Blur(current, null, filters.Blur.BlurX, filters.Blur.BlurY);
            if (Visible(filters.Glow.Enabled, filters.Glow.Strength, filters.Glow.Opacity, filters.Glow.ColorArgb))
                Behind(filters.Glow.BlurX, filters.Glow.BlurY, filters.Glow.ColorArgb, filters.Glow.Strength, filters.Glow.Opacity, 0, 0);
            if (Visible(filters.Shadow.Enabled, filters.Shadow.Strength, filters.Shadow.Opacity, filters.Shadow.ColorArgb))
            {
                var angle = filters.Shadow.Angle * (Math.PI / 180);
                var distance = (double)filters.Shadow.Distance * scale;
                Behind(filters.Shadow.BlurX, filters.Shadow.BlurY, filters.Shadow.ColorArgb, filters.Shadow.Strength, filters.Shadow.Opacity,
                    (float)(Math.Cos(angle) * distance), (float)(Math.Sin(angle) * distance));
            }
            Edge(filters.Bevel, 3);
            Edge(filters.GradientBevel, 4);
            Edge(filters.GradientGlow, 5);
            Dispatch(current, null, _output!, new(new(width, height, 2, 0), default, default, default));
            _context!.CopyResource(_readback!, _output!.Resource);
            var mapped = _context.Map(_readback!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                // Commit only after every dispatch/readback succeeds. The CPU fallback
                // rereads the bitmap if any part of the readback fails.
                for (int y = 0; y < height; y++)
                    Marshal.Copy(mapped.DataPointer + checked((int)mapped.RowPitch * y), bytes, y * width * 4, width * 4);
            }
            finally { _context.Unmap(_readback!, 0); }
            CompletedApplications++;
            if (entry is not null)
            {
                bytes.AsSpan(0, byteCount).CopyTo(entry.Output);
                entry.Width = width; entry.Height = height; entry.Filters = filters; entry.Scale = scale;
                _results.Add(entry);
            }
            return true;

            Texture Blur(Texture source, Texture? preserve, float x, float y)
            {
                for (int pass = 0; pass < 3; pass++)
                {
                    if (x > 0) Axis((float)((double)x * scale / 3), false);
                    if (y > 0) Axis((float)((double)y * scale / 3), true);
                }
                return source;
                void Axis(float radius, bool vertical)
                {
                    var target = Available(source, preserve);
                    Dispatch(source, null, target, new(new(width, height, 0, vertical ? 1 : 0), new(radius, 0, 0, 0), default, default), radius > 32);
                    source = target;
                }
            }
            void Edge(SymbolEdgeFilter filter, int mode)
            {
                if (!filter.Enabled || filter.Strength == 0 || filter.Opacity == 0) return;
                var mask = Blur(current, current, filter.BlurX, filter.BlurY);
                var target = Available(current, mask);
                double angle = filter.Angle * Math.PI / 180;
                double distance = (double)filter.Distance * scale;
                Dispatch(current, mask, target, new(new(width, height, mode, 0),
                    new(filter.Opacity, 0, 0, 0), ColorVector(filter.StartColorArgb),
                    new((float)(Math.Cos(angle) * distance), (float)(Math.Sin(angle) * distance), filter.Strength, 0),
                    ColorVector(filter.EndColorArgb)));
                current = target;
            }
            void Behind(float x, float y, int argb, float strength, float opacity, float dx, float dy)
            {
                var mask = Blur(current, current, x, y);
                var target = Available(current, mask);
                uint color = (uint)argb;
                // Texture channels deliberately hold BGRA, matching the PArgb bytes.
                var tint = new Vector4((color & 255) / 255f, ((color >> 8) & 255) / 255f, ((color >> 16) & 255) / 255f, opacity * (color >> 24) / 255f);
                Dispatch(current, mask, target, new(new(width, height, 1, 0), default, tint, new(dx, dy, strength, 0)));
                current = target;
            }
        }
        catch (Exception exception)
        {
            LastFailure = exception.Message;
            AppLog.Warn($"GPU symbol filters unavailable; using CPU filters: {exception.Message}");
            _failed = true;
            ReleaseResources();
            return false;
        }
    }

    private static Vector4 ColorVector(int argb)
    {
        uint color = (uint)argb;
        return new((color & 255) / 255f, ((color >> 8) & 255) / 255f,
            ((color >> 16) & 255) / 255f, (color >> 24) / 255f);
    }

    private static bool Visible(bool enabled, float strength, float opacity, int argb)
        => enabled && strength > 0 && opacity > 0 && ((uint)argb >> 24) != 0;
    private Texture Available(Texture first, Texture? second)
        => _work.First(item => item != first && item != second)!;

    private void EnsureDevice()
    {
        if (_device is not null) return;
        // Match the Stage preference on hybrid laptops; default D3D creation
        // often selects the integrated adapter even when a faster GPU exists.
        using (var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
        {
            using var preference = factory.QueryInterfaceOrNull<IDXGIFactory6>();
            for (uint index = 0; ; index++)
            {
                IDXGIAdapter1? adapter;
                var result = preference is not null
                    ? preference.EnumAdapterByGpuPreference(index, GpuPreference.HighPerformance, out adapter)
                    : factory.EnumAdapters1(index, out adapter);
                if (result.Failure || adapter is null) break;
                using (adapter)
                {
                    if ((adapter.Description1.Flags & AdapterFlags.Software) != 0) continue;
                    result = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.None,
                        new[] { FeatureLevel.Level_11_0 }, out _device, out _, out _context);
                    if (result.Success) break;
                    _context?.Dispose(); _context = null;
                    _device?.Dispose(); _device = null;
                }
            }
        }
        if (_device is null)
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_0 }, out _device, out _, out _context).CheckError();
        using (var dxgi = _device!.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgi.GetAdapter()) AdapterName = adapter.Description.Description;
        _pixels = _device.CreateComputeShader(Bytecode.Value[0], null);
        _lines = _device.CreateComputeShader(Bytecode.Value[1], null);
        _constants = _device.CreateBuffer(new Constants[1], BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0, 0);
    }

    private void EnsureSurfaces(int width, int height)
    {
        if (_input is not null && width <= _capacityWidth && height <= _capacityHeight
            && (long)_capacityWidth * _capacityHeight <= Math.Max(65536L, (long)width * height * 4)) return;
        ReleaseSurfaces();
        _capacityWidth = (width + 63) / 64 * 64;
        _capacityHeight = (height + 63) / 64 * 64;
        if ((long)_capacityWidth * _capacityHeight > MaximumPixels)
        { _capacityWidth = width; _capacityHeight = height; }
        _input = new Texture(_device!, _capacityWidth, _capacityHeight, Format.R8G8B8A8_UNorm, false);
        _output = new Texture(_device!, _capacityWidth, _capacityHeight, Format.R8G8B8A8_UNorm, true);
        for (int i = 0; i < _work.Length; i++)
            _work[i] = new Texture(_device!, _capacityWidth, _capacityHeight, Format.R32G32B32A32_Float, true);
        _readback = _device!.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm,
            (uint)_capacityWidth, (uint)_capacityHeight, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));
    }

    private void Dispatch(Texture source, Texture? mask, Texture target, Constants constants, bool lines = false)
    {
        _context!.UpdateSubresource(in constants, _constants!, 0, 0, 0, null);
        _context.CSSetShader(lines ? _lines! : _pixels!);
        _context.CSSetConstantBuffer(0, _constants!);
        _context.CSSetShaderResources(0, new[] { source.View, mask?.View! });
        _context.CSSetUnorderedAccessView(0, target.Write!, 0);
        try
        {
            if (lines) _context.Dispatch((uint)(((constants.Bounds.W == 0 ? constants.Bounds.Y : constants.Bounds.X) + 63) / 64), 1, 1);
            else _context.Dispatch((uint)((constants.Bounds.X + 15) / 16), (uint)((constants.Bounds.Y + 15) / 16), 1);
        }
        finally
        {
            _context.CSSetShaderResources(0, new ID3D11ShaderResourceView[] { null!, null! });
            _context.CSSetUnorderedAccessView(0, null!, 0);
            _context.CSSetConstantBuffer(0, null!);
            _context.CSSetShader(null!);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Constants(Vector4 Bounds, Vector4 Kernel, Vector4 Color, Vector4 Effect, Vector4 EndColor = default);
    private sealed class Texture : IDisposable
    {
        internal readonly ID3D11Texture2D Resource;
        internal readonly ID3D11ShaderResourceView View;
        internal readonly ID3D11UnorderedAccessView? Write;
        internal Texture(ID3D11Device device, int width, int height, Format format, bool writable)
        {
            Resource = device.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1,
                BindFlags.ShaderResource | (writable ? BindFlags.UnorderedAccess : BindFlags.None), ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
            try
            {
                View = device.CreateShaderResourceView(Resource, null);
                try { if (writable) Write = device.CreateUnorderedAccessView(Resource, null); }
                catch { View.Dispose(); throw; }
            }
            catch { Resource.Dispose(); throw; }
        }
        public void Dispose() { Write?.Dispose(); View.Dispose(); Resource.Dispose(); }
    }
    private void ReleaseSurfaces()
    {
        _readback?.Dispose(); _readback = null;
        _input?.Dispose(); _input = null;
        _output?.Dispose(); _output = null;
        for (int i = 0; i < _work.Length; i++) { _work[i]?.Dispose(); _work[i] = null; }
    }
    private void ReleaseResources()
    {
        _results.Clear();
        _context?.ClearState();
        ReleaseSurfaces();
        _constants?.Dispose(); _constants = null;
        _pixels?.Dispose(); _pixels = null;
        _lines?.Dispose(); _lines = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; ReleaseResources(); }

    private static byte[] Compile(string source, string entry)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        IntPtr code = IntPtr.Zero;
        IntPtr errors = IntPtr.Zero;
        try
        {
            var hr = D3DCompile(
                sourceBytes,
                (UIntPtr)sourceBytes.Length,
                "SymbolFilters.hlsl",
                IntPtr.Zero,
                IntPtr.Zero,
                entry,
                "cs_5_0",
                (1u << 11) | (3u << 14),
                0,
                out code,
                out errors);
            if (hr < 0)
            {
                var message = errors == IntPtr.Zero
                    ? $"HRESULT 0x{hr:X8}"
                    : Marshal.PtrToStringAnsi(BlobPointer(errors)) ?? $"HRESULT 0x{hr:X8}";
                throw new InvalidOperationException(
                    $"The GPU filter compute shader failed to compile: {message}");
            }

            var pointer = BlobPointer(code);
            var length = checked((int)BlobSize(code));
            var bytecode = new byte[length];
            Marshal.Copy(pointer, bytecode, 0, length);
            return bytecode;
        }
        finally
        {
            ReleaseBlob(code);
            ReleaseBlob(errors);
        }
    }

    private static IntPtr BlobPointer(IntPtr blob)
    {
        if (blob == IntPtr.Zero) return IntPtr.Zero;
        var vtable = Marshal.ReadIntPtr(blob);
        var address = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
        var getPointer = Marshal.GetDelegateForFunctionPointer<GetBlobPointer>(address);
        return getPointer(blob);
    }

    private static UIntPtr BlobSize(IntPtr blob)
    {
        if (blob == IntPtr.Zero) return UIntPtr.Zero;
        var vtable = Marshal.ReadIntPtr(blob);
        var address = Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size);
        var getSize = Marshal.GetDelegateForFunctionPointer<GetBlobSize>(address);
        return getSize(blob);
    }

    private static void ReleaseBlob(IntPtr blob)
    {
        if (blob == IntPtr.Zero) return;
        var vtable = Marshal.ReadIntPtr(blob);
        var address = Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size);
        var release = Marshal.GetDelegateForFunctionPointer<ReleaseComObject>(address);
        _ = release(blob);
    }


    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(
        byte[] sourceData,
        UIntPtr sourceDataSize,
        [MarshalAs(UnmanagedType.LPStr)] string sourceName,
        IntPtr defines,
        IntPtr include,
        [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
        [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errorMessages);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBlobPointer(IntPtr blob);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate UIntPtr GetBlobSize(IntPtr blob);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseComObject(IntPtr blob);


    private const string ShaderSource = """
        cbuffer Settings : register(b0) { float4 bounds; float4 kernel; float4 tint; float4 effect; float4 endTint; };
        Texture2D<float4> source : register(t0);
        Texture2D<float4> mask : register(t1);
        RWTexture2D<float4> target : register(u0);
        float4 Read(int2 p) {
            if (any(p < 0) || any(p >= (int2)bounds.xy)) return 0;
            float4 v = source.Load(int3(p, 0));
            return float4(min(v.xyz, v.www), v.w);
        }
        float Alpha(int2 p) {
            if (any(p < 0) || any(p >= (int2)bounds.xy)) return 0;
            return mask.Load(int3(p, 0)).w;
        }
        float SampleAlpha(float2 q) {
            int2 a = (int2)floor(q); float2 f = q - a;
            return lerp(lerp(Alpha(a), Alpha(a+int2(1,0)), f.x), lerp(Alpha(a+int2(0,1)), Alpha(a+1), f.x), f.y);
        }
        [numthreads(16,16,1)]
        void Pixels(uint3 id : SV_DispatchThreadID) {
            int2 p = id.xy;
            if (any(p >= (int2)bounds.xy)) return;
            float4 value = Read(p);
            if (bounds.z == 0) {
                int2 axis = bounds.w == 0 ? int2(1,0) : int2(0,1);
                int radius = (int)floor(kernel.x);
                float fraction = kernel.x - radius;
                value = 0;
                for (int i = -radius; i <= radius; i++) value += Read(p + axis * i);
                value += fraction * (Read(p - axis * (radius + 1)) + Read(p + axis * (radius + 1)));
                value = saturate(value / (2 * kernel.x + 1));
            } else if (bounds.z == 1) {
                float2 q = (float2)p - effect.xy;
                int2 a = (int2)floor(q); float2 f = q - a;
                float alpha = lerp(lerp(Alpha(a), Alpha(a+int2(1,0)), f.x), lerp(Alpha(a+int2(0,1)), Alpha(a+1), f.x), f.y);
                float behind = saturate(alpha * effect.z) * tint.w * (1 - value.w);
                value += float4(tint.xyz, 1) * behind;
            } else if (bounds.z >= 3) {
                bool glow = bounds.z == 5;
                float signal = glow ? Alpha(p) : SampleAlpha((float2)p + effect.xy) - SampleAlpha((float2)p - effect.xy);
                float t = glow ? saturate(signal) : bounds.z == 4 ? (signal + 1) * 0.5 : signal >= 0 ? 1 : 0;
                float4 color = lerp(tint, endTint, t);
                float alpha = saturate(abs(signal) * effect.z) * kernel.x * color.w;
                alpha *= glow ? 1 - value.w : value.w;
                float keep = glow ? 1 : 1 - alpha / max(value.w, 1e-10);
                value.xyz = value.xyz * keep + color.xyz * alpha;
                if (glow) value.w += alpha;
            } else {
                // Match the CPU's half-up final quantization; keep float tails until here.
                value = floor(saturate(value) * 255 + 0.5) / 255;
                value.xyz = min(value.xyz, value.www);
            }
            target[p] = value;
        }
        // Large radii use sliding sums, so cost stays O(area), not O(area*radius).
        [numthreads(64,1,1)]
        void Lines(uint3 id : SV_DispatchThreadID) {
            bool vertical = bounds.w != 0;
            int length = vertical ? bounds.y : bounds.x;
            int count = vertical ? bounds.x : bounds.y;
            if (id.x >= count) return;
            int2 axis = vertical ? int2(0,1) : int2(1,0);
            int2 start = vertical ? int2(id.x,0) : int2(0,id.x);
            int radius = (int)floor(kernel.x);
            float fraction = kernel.x - radius;
            float4 sum = 0;
            for (int i=0; i<=min(radius,length-1); i++) sum += Read(start+axis*i);
            for (int pos=0; pos<length; pos++) {
                float4 value = sum + fraction * (Read(start+axis*(pos-radius-1)) + Read(start+axis*(pos+radius+1)));
                target[start+axis*pos] = saturate(value / (2*kernel.x+1));
                sum -= Read(start+axis*(pos-radius));
                sum += Read(start+axis*(pos+radius+1));
            }
        }
        """;
}
