using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VectorAnimationEngine;

/// <summary>
/// Applies the reference 3D optical pass on the GPU without copying pixels to
/// the CPU.  The host owns the D3D11 and D2D devices; this type only owns the
/// compute shader and its per-surface resources.
/// </summary>
internal sealed class StageGpuOpticalShader : IDisposable
{
    private const uint CompileStrictness = 1u << 11;
    private const uint CompileOptimizationLevel3 = 3u << 14;
    private const int ThreadsPerGroup = 16;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _computeContext;
    private readonly ID2D1DeviceContext _d2dContext;
    private readonly ID3D11ComputeShader _computeShader;
    private readonly ID3D11Buffer _constantBuffer;
    private bool _disposed;

    public StageGpuOpticalShader(StageGpuDevice host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _device = host.Device;
        _computeContext = host.ImmediateContext;
        _d2dContext = host.Context;

        var bytecode = CompileComputeShader(ShaderSource);
        _computeShader = _device.CreateComputeShader(bytecode, null);
        _constantBuffer = _device.CreateBuffer(
            new ShaderConstants[1],
            BindFlags.ConstantBuffer,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0,
            0);
    }

    public Surface CreateSurface(Size size)
    {
        ThrowIfDisposed();
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }
        return new Surface(this, size.Width, size.Height);
    }

    public void Shade(
        Surface surface,
        Reference3DRenderItem item,
        SpatialOpticalMaterial material,
        Rectangle screenBounds)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(surface);
        surface.ThrowIfDisposed();
        if (!ReferenceEquals(surface.Owner, this))
        {
            throw new ArgumentException("The optical surface belongs to another GPU shader.", nameof(surface));
        }
        if (screenBounds.Width <= 0
            || screenBounds.Height <= 0
            || screenBounds.Width > surface.Width
            || screenBounds.Height > surface.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(screenBounds));
        }

        var normalized = material.Normalize();
        var layers = BuildGpuLayers(item.LocalLightLayers, out var edges, out var stops);
        surface.EnsureStructuredBuffers(layers, edges, stops);
        var constants = new ShaderConstants(
            new Vector4(
                screenBounds.Left,
                screenBounds.Top,
                screenBounds.Width,
                screenBounds.Height),
            new Vector4(
                FiniteOrZero(item.OpticalResponse.AmbientIrradiance).X,
                FiniteOrZero(item.OpticalResponse.AmbientIrradiance).Y,
                FiniteOrZero(item.OpticalResponse.AmbientIrradiance).Z,
                normalized.Metallic),
            new Vector4(
                DielectricF0(normalized),
                layers.Length,
                edges.Length,
                stops.Length));

        _computeContext.UpdateSubresource(
            in constants,
            _constantBuffer,
            0,
            0,
            0,
            null);
        _computeContext.CSSetShader(_computeShader);
        _computeContext.CSSetConstantBuffer(0, _constantBuffer);
        _computeContext.CSSetShaderResources(
            0,
            [surface.BaseShaderResourceView,
                surface.LayerShaderResourceView!,
                surface.EdgeShaderResourceView!,
                surface.StopShaderResourceView!]);
        _computeContext.CSSetUnorderedAccessView(0, surface.OutputUnorderedAccessView, 0);
        try
        {
            _computeContext.Dispatch(
                (uint)((screenBounds.Width + ThreadsPerGroup - 1) / ThreadsPerGroup),
                (uint)((screenBounds.Height + ThreadsPerGroup - 1) / ThreadsPerGroup),
                1);
        }
        finally
        {
            // D2D must be able to bind the output bitmap immediately after the
            // dispatch, so release every D3D11 binding before disposing views.
            _computeContext.CSSetShaderResources(
                0,
                [null!, null!, null!, null!]);
            _computeContext.CSSetUnorderedAccessView(0, null!, 0);
            _computeContext.CSSetConstantBuffer(0, null!);
            _computeContext.CSSetShader(null!);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _constantBuffer.Dispose();
        _computeShader.Dispose();
    }

    private ID3D11Buffer CreateStructuredBuffer<T>(T[] data, uint stride)
        where T : unmanaged
    {
        if (data.Length == 0) data = new T[1];
        return _device.CreateBuffer(
            data,
            BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            ResourceOptionFlags.BufferStructured,
            stride,
            0);
    }



    private static byte[] CompileComputeShader(string source)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        IntPtr code = IntPtr.Zero;
        IntPtr errors = IntPtr.Zero;
        try
        {
            var hr = D3DCompile(
                sourceBytes,
                (UIntPtr)sourceBytes.Length,
                "StageGpuOpticalShader.hlsl",
                IntPtr.Zero,
                IntPtr.Zero,
                "main",
                "cs_5_0",
                CompileStrictness | CompileOptimizationLevel3,
                0,
                out code,
                out errors);
            if (hr < 0)
            {
                var message = errors == IntPtr.Zero
                    ? $"HRESULT 0x{hr:X8}"
                    : Marshal.PtrToStringAnsi(BlobPointer(errors)) ?? $"HRESULT 0x{hr:X8}";
                throw new InvalidOperationException(
                    $"The GPU optical compute shader failed to compile: {message}");
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

    private static Vector3 FiniteOrZero(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
            ? value
            : Vector3.Zero;

    private static float DielectricF0(SpatialOpticalMaterial material)
    {
        var ratio = (material.IndexOfRefraction - 1f)
            / (material.IndexOfRefraction + 1f);
        return Math.Clamp(Math.Max(material.Reflectivity, ratio * ratio), 0f, 1f);
    }

    private static GpuLayer[] BuildGpuLayers(
        IReadOnlyList<Reference3DLocalLightLayer>? source,
        out GpuEdge[] edges,
        out GpuStop[] stops)
    {
        var edgeList = new List<GpuEdge>();
        var stopList = new List<GpuStop>();
        var layerList = new List<GpuLayer>();
        foreach (var layer in source ?? [])
        {
            if (layer.LinearStops.Length == 0
                || !Matrix3x2.Invert(layer.GradientTransform, out var inverse))
            {
                continue;
            }

            var edgeStart = edgeList.Count;
            var left = float.PositiveInfinity;
            var top = float.PositiveInfinity;
            var right = float.NegativeInfinity;
            var bottom = float.NegativeInfinity;
            foreach (var contour in layer.Contours)
            {
                if (!contour.Closed || contour.Points.Length < 3) continue;
                for (var index = 0; index < contour.Points.Length; index++)
                {
                    var a = contour.Points[index];
                    var b = contour.Points[(index + 1) % contour.Points.Length];
                    if (!float.IsFinite(a.X)
                        || !float.IsFinite(a.Y)
                        || !float.IsFinite(b.X)
                        || !float.IsFinite(b.Y))
                    {
                        continue;
                    }
                    edgeList.Add(new GpuEdge(
                        new Vector2(a.X, a.Y),
                        new Vector2(b.X, b.Y)));
                    left = MathF.Min(left, MathF.Min(a.X, b.X));
                    top = MathF.Min(top, MathF.Min(a.Y, b.Y));
                    right = MathF.Max(right, MathF.Max(a.X, b.X));
                    bottom = MathF.Max(bottom, MathF.Max(a.Y, b.Y));
                }
            }
            var edgeCount = edgeList.Count - edgeStart;
            if (edgeCount == 0) continue;

            var stopStart = stopList.Count;
            foreach (var stop in layer.LinearStops)
            {
                var diffuse = FiniteOrZero(stop.DiffuseIrradiance);
                var specular = FiniteOrZero(stop.SpecularRadiance);
                var fresnel = FiniteOrZero(stop.FresnelRadiance);
                stopList.Add(new GpuStop(
                    new Vector4(
                        float.IsFinite(stop.Position) ? stop.Position : 1f,
                        0f,
                        0f,
                        0f),
                    new Vector4(diffuse, 0f),
                    new Vector4(specular, 0f),
                    new Vector4(fresnel, 0f)));
            }
            var stopCount = stopList.Count - stopStart;
            if (stopCount == 0)
            {
                edgeList.RemoveRange(edgeStart, edgeCount);
                continue;
            }

            layerList.Add(new GpuLayer(
                (uint)edgeStart,
                (uint)edgeCount,
                (uint)stopStart,
                (uint)stopCount,
                new Vector4(inverse.M11, inverse.M21, inverse.M31, 0f),
                new Vector4(inverse.M12, inverse.M22, inverse.M32, 0f),
                new Vector4(left, top, right, bottom)));
        }

        edges = edgeList.Count == 0 ? [new GpuEdge()] : edgeList.ToArray();
        stops = stopList.Count == 0 ? [new GpuStop()] : stopList.ToArray();
        return layerList.ToArray();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StageGpuOpticalShader));
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

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ShaderConstants
    {
        public ShaderConstants(Vector4 bounds, Vector4 ambientMetallic, Vector4 dielectricCounts)
        {
            Bounds = bounds;
            AmbientMetallic = ambientMetallic;
            DielectricCounts = dielectricCounts;
        }

        public readonly Vector4 Bounds;
        public readonly Vector4 AmbientMetallic;
        public readonly Vector4 DielectricCounts;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct GpuLayer
    {
        public const uint Size = 64;

        public GpuLayer(
            uint edgeStart,
            uint edgeCount,
            uint stopStart,
            uint stopCount,
            Vector4 inverseX,
            Vector4 inverseY,
            Vector4 bounds)
        {
            EdgeStart = edgeStart;
            EdgeCount = edgeCount;
            StopStart = stopStart;
            StopCount = stopCount;
            InverseX = inverseX;
            InverseY = inverseY;
            Bounds = bounds;
        }

        public readonly uint EdgeStart;
        public readonly uint EdgeCount;
        public readonly uint StopStart;
        public readonly uint StopCount;
        public readonly Vector4 InverseX;
        public readonly Vector4 InverseY;
        public readonly Vector4 Bounds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct GpuEdge
    {
        public const uint Size = 32;

        public GpuEdge(Vector2 a, Vector2 b)
        {
            A = new Vector4(a, 0f, 0f);
            B = new Vector4(b, 0f, 0f);
        }

        public readonly Vector4 A;
        public readonly Vector4 B;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct GpuStop
    {
        public const uint Size = 64;

        public GpuStop(Vector4 position, Vector4 diffuse, Vector4 specular, Vector4 fresnel)
        {
            Position = position;
            Diffuse = diffuse;
            Specular = specular;
            Fresnel = fresnel;
        }

        public readonly Vector4 Position;
        public readonly Vector4 Diffuse;
        public readonly Vector4 Specular;
        public readonly Vector4 Fresnel;
    }

    internal sealed class Surface : IDisposable
    {
        private readonly ID3D11Texture2D _baseTexture;
        private readonly ID3D11Texture2D _outputTexture;
        private readonly ID3D11ShaderResourceView _baseShaderResourceView;
        private readonly ID3D11UnorderedAccessView _outputUnorderedAccessView;
        private ID3D11Buffer? _layerBuffer;
        private ID3D11Buffer? _edgeBuffer;
        private ID3D11Buffer? _stopBuffer;
        private ID3D11ShaderResourceView? _layerShaderResourceView;
        private ID3D11ShaderResourceView? _edgeShaderResourceView;
        private ID3D11ShaderResourceView? _stopShaderResourceView;
        private GpuLayer[]? _layers;
        private GpuEdge[]? _edges;
        private GpuStop[]? _stops;
        private bool _disposed;

        internal Surface(StageGpuOpticalShader owner, int width, int height)
        {
            Owner = owner;
            Width = width;
            Height = height;
            var baseDescription = new Texture2DDescription(
                Format.B8G8R8A8_UNorm,
                (uint)width,
                (uint)height,
                1,
                1,
                BindFlags.RenderTarget | BindFlags.ShaderResource,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                1,
                0,
                ResourceOptionFlags.None);
            var outputDescription = new Texture2DDescription(
                Format.R8G8B8A8_UNorm,
                (uint)width,
                (uint)height,
                1,
                1,
                BindFlags.ShaderResource | BindFlags.UnorderedAccess,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                1,
                0,
                ResourceOptionFlags.None);

            _baseTexture = owner._device.CreateTexture2D(baseDescription);
            try
            {
                _outputTexture = owner._device.CreateTexture2D(outputDescription);
                try
                {
                    _baseShaderResourceView = owner._device.CreateShaderResourceView(_baseTexture, null);
                    try
                    {
                        _outputUnorderedAccessView = owner._device.CreateUnorderedAccessView(_outputTexture, null);
                        try
                        {
                            BaseBitmap = CreateD2DBitmap(
                                _baseTexture,
                                Format.B8G8R8A8_UNorm,
                                BitmapOptions.Target);
                            try
                            {
                                OutputBitmap = CreateD2DBitmap(
                                    _outputTexture,
                                    Format.R8G8B8A8_UNorm,
                                    BitmapOptions.None);
                            }
                            catch
                            {
                                BaseBitmap.Dispose();
                                throw;
                            }
                        }
                        catch
                        {
                            _outputUnorderedAccessView.Dispose();
                            throw;
                        }
                    }
                    catch
                    {
                        _baseShaderResourceView.Dispose();
                        throw;
                    }
                }
                catch
                {
                    _outputTexture.Dispose();
                    throw;
                }
            }
            catch
            {
                _baseTexture.Dispose();
                throw;
            }
        }

        internal StageGpuOpticalShader Owner { get; }

        internal int Width { get; }

        internal int Height { get; }

        internal ID2D1Bitmap1 BaseBitmap { get; }

        internal ID2D1Bitmap1 OutputBitmap { get; }

        internal ID3D11ShaderResourceView BaseShaderResourceView => _baseShaderResourceView;

        internal ID3D11UnorderedAccessView OutputUnorderedAccessView => _outputUnorderedAccessView;

        internal ID3D11ShaderResourceView? LayerShaderResourceView => _layerShaderResourceView;

        internal ID3D11ShaderResourceView? EdgeShaderResourceView => _edgeShaderResourceView;

        internal ID3D11ShaderResourceView? StopShaderResourceView => _stopShaderResourceView;

        internal void EnsureStructuredBuffers(
            GpuLayer[] layers,
            GpuEdge[] edges,
            GpuStop[] stops)
        {
            ThrowIfDisposed();
            if (_layers is null || !_layers.AsSpan().SequenceEqual(layers))
            {
                DisposeLayerBuffers();
                _layerBuffer = Owner.CreateStructuredBuffer(layers, GpuLayer.Size);
                _layerShaderResourceView = Owner._device.CreateShaderResourceView(_layerBuffer, null);
                _layers = layers;
            }
            if (_edges is null || !_edges.AsSpan().SequenceEqual(edges))
            {
                DisposeEdgeBuffers();
                _edgeBuffer = Owner.CreateStructuredBuffer(edges, GpuEdge.Size);
                _edgeShaderResourceView = Owner._device.CreateShaderResourceView(_edgeBuffer, null);
                _edges = edges;
            }
            if (_stops is null || !_stops.AsSpan().SequenceEqual(stops))
            {
                DisposeStopBuffers();
                _stopBuffer = Owner.CreateStructuredBuffer(stops, GpuStop.Size);
                _stopShaderResourceView = Owner._device.CreateShaderResourceView(_stopBuffer, null);
                _stops = stops;
            }
        }

        private void DisposeLayerBuffers()
        {
            _layerShaderResourceView?.Dispose();
            _layerShaderResourceView = null;
            _layerBuffer?.Dispose();
            _layerBuffer = null;
        }

        private void DisposeEdgeBuffers()
        {
            _edgeShaderResourceView?.Dispose();
            _edgeShaderResourceView = null;
            _edgeBuffer?.Dispose();
            _edgeBuffer = null;
        }

        private void DisposeStopBuffers()
        {
            _stopShaderResourceView?.Dispose();
            _stopShaderResourceView = null;
            _stopBuffer?.Dispose();
            _stopBuffer = null;
        }

        private ID2D1Bitmap1 CreateD2DBitmap(
            ID3D11Texture2D texture,
            Format format,
            BitmapOptions options)
        {
            using var surface = texture.QueryInterface<IDXGISurface>();
            return Owner._d2dContext.CreateBitmapFromDxgiSurface(
                surface,
                new BitmapProperties1(
                    new PixelFormat(format, Vortice.DCommon.AlphaMode.Premultiplied),
                    96f,
                    96f,
                    options));
        }

        internal void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Surface));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisposeLayerBuffers();
            DisposeEdgeBuffers();
            DisposeStopBuffers();
            OutputBitmap.Dispose();
            BaseBitmap.Dispose();
            _outputUnorderedAccessView.Dispose();
            _baseShaderResourceView.Dispose();
            _outputTexture.Dispose();
            _baseTexture.Dispose();
        }
    }

    private const string ShaderSource = """
        struct GpuLayer
        {
            uint EdgeStart;
            uint EdgeCount;
            uint StopStart;
            uint StopCount;
            float4 InverseX;
            float4 InverseY;
            float4 Bounds;
        };

        struct GpuEdge
        {
            float4 A;
            float4 B;
        };

        struct GpuStop
        {
            float4 Position;
            float4 Diffuse;
            float4 Specular;
            float4 Fresnel;
        };

        cbuffer ShaderConstants : register(b0)
        {
            float4 Bounds;
            float4 AmbientMetallic;
            float4 DielectricCounts;
        };

        Texture2D<float4> BaseTexture : register(t0);
        StructuredBuffer<GpuLayer> Layers : register(t1);
        StructuredBuffer<GpuEdge> Edges : register(t2);
        StructuredBuffer<GpuStop> Stops : register(t3);
        RWTexture2D<float4> OutputTexture : register(u0);

        groupshared uint ActiveLayerCount;
        groupshared uint ActiveLayerIndices[32];

        float SrgbToLinear(float value)
        {
            return value <= 0.04045f
                ? value / 12.92f
                : pow((value + 0.055f) / 1.055f, 2.4f);
        }

        float LinearToSrgb(float value)
        {
            value = max(0.0f, value);
            return value <= 0.0031308f
                ? value * 12.92f
                : 1.055f * pow(value, 1.0f / 2.4f) - 0.055f;
        }

        uint ToByte(float value)
        {
            return (uint)round(saturate(LinearToSrgb(value)) * 255.0f);
        }

        uint Quantize(float value)
        {
            return (uint)round(saturate(value) * 255.0f);
        }

        uint Unpremultiply(uint value, uint alpha)
        {
            return min(255u, (value * 255u + alpha / 2u) / alpha);
        }

        uint Premultiply(uint value, uint alpha)
        {
            return (value * alpha + 127u) / 255u;
        }

        bool ContainsEvenOdd(GpuLayer layer, float2 samplePoint)
        {
            if (samplePoint.x < layer.Bounds.x
                || samplePoint.y < layer.Bounds.y
                || samplePoint.x > layer.Bounds.z
                || samplePoint.y > layer.Bounds.w)
            {
                return false;
            }

            bool inside = false;
            for (uint index = 0; index < layer.EdgeCount; index++)
            {
                GpuEdge edge = Edges[layer.EdgeStart + index];
                bool crossing = (edge.A.y <= samplePoint.y && edge.B.y > samplePoint.y)
                    || (edge.B.y <= samplePoint.y && edge.A.y > samplePoint.y);
                if (!crossing) continue;
                float denominator = edge.B.y - edge.A.y;
                if (abs(denominator) <= 0.0000001f) continue;
                float intersection = edge.A.x
                    + (samplePoint.y - edge.A.y) * (edge.B.x - edge.A.x) / denominator;
                if (intersection > samplePoint.x) inside = !inside;
            }
            return inside;
        }

        GpuStop SampleStops(GpuLayer layer, float position)
        {
            position = saturate(position);
            GpuStop first = Stops[layer.StopStart];
            if (layer.StopCount <= 1 || position <= first.Position.x) return first;
            GpuStop previous = first;
            for (uint index = 1; index < layer.StopCount; index++)
            {
                GpuStop current = Stops[layer.StopStart + index];
                if (position <= current.Position.x)
                {
                    float distance = current.Position.x - previous.Position.x;
                    float amount = distance <= 0.000001f
                        ? 0.0f
                        : saturate((position - previous.Position.x) / distance);
                    GpuStop result;
                    result.Position = float4(position, 0, 0, 0);
                    result.Diffuse = lerp(previous.Diffuse, current.Diffuse, amount);
                    result.Specular = lerp(previous.Specular, current.Specular, amount);
                    result.Fresnel = lerp(previous.Fresnel, current.Fresnel, amount);
                    return result;
                }
                previous = current;
            }
            return previous;
        }

        [numthreads(16, 16, 1)]
        void main(uint3 dispatchThreadId : SV_DispatchThreadID, uint3 groupId : SV_GroupID, uint groupIndex : SV_GroupIndex)
        {
            if (groupIndex == 0)
            {
                uint layerCount = min((uint)DielectricCounts.y, 32u);
                float2 tileMin = float2(
                    Bounds.x + groupId.x * 16.0f,
                    Bounds.y + groupId.y * 16.0f);
                float2 tileMax = tileMin + 16.0f;
                uint activeCount = 0u;
                for (uint index = 0; index < layerCount; index++)
                {
                    GpuLayer layer = Layers[index];
                    bool overlaps = layer.Bounds.z >= tileMin.x
                        && layer.Bounds.x <= tileMax.x
                        && layer.Bounds.w >= tileMin.y
                        && layer.Bounds.y <= tileMax.y;
                    if (overlaps) ActiveLayerIndices[activeCount++] = index;
                }
                ActiveLayerCount = activeCount;
            }
            GroupMemoryBarrierWithGroupSync();

            uint2 pixel = dispatchThreadId.xy;
            uint width = (uint)Bounds.z;
            uint height = (uint)Bounds.w;
            if (pixel.x >= width || pixel.y >= height) return;

            float4 baseSample = BaseTexture.Load(int3(pixel, 0));
            uint alpha = Quantize(baseSample.a);
            if (alpha == 0u)
            {
                OutputTexture[pixel] = float4(0, 0, 0, 0);
                return;
            }

            uint red = Unpremultiply(Quantize(baseSample.r), alpha);
            uint green = Unpremultiply(Quantize(baseSample.g), alpha);
            uint blue = Unpremultiply(Quantize(baseSample.b), alpha);
            float3 baseLinear = float3(
                SrgbToLinear(red / 255.0f),
                SrgbToLinear(green / 255.0f),
                SrgbToLinear(blue / 255.0f));
            float3 diffuse = AmbientMetallic.xyz;
            float3 specular = 0.0f;
            float3 fresnel = 0.0f;
            float2 screen = float2(
                Bounds.x + pixel.x + 0.5f,
                Bounds.y + pixel.y + 0.5f);

            for (uint activeIndex = 0; activeIndex < ActiveLayerCount; activeIndex++)
            {
                uint layerIndex = ActiveLayerIndices[activeIndex];
                GpuLayer layer = Layers[layerIndex];
                if (!ContainsEvenOdd(layer, screen)) continue;
                float2 gradientPoint = float2(
                    dot(screen, layer.InverseX.xy) + layer.InverseX.z,
                    dot(screen, layer.InverseY.xy) + layer.InverseY.z);
                float position = saturate(length(gradientPoint));
                GpuStop light = SampleStops(layer, position);
                diffuse += light.Diffuse.xyz;
                specular += light.Specular.xyz;
                fresnel += light.Fresnel.xyz;
            }

            float metallic = saturate(AmbientMetallic.w);
            float dielectricF0 = saturate(DielectricCounts.x);
            float3 specularTint = lerp(
                dielectricF0.xxx,
                baseLinear,
                metallic);
            float3 outputLinear = max(
                0.0f,
                baseLinear * diffuse + specular * specularTint + fresnel);
            uint outRed = ToByte(outputLinear.r);
            uint outGreen = ToByte(outputLinear.g);
            uint outBlue = ToByte(outputLinear.b);
            OutputTexture[pixel] = float4(
                Premultiply(outRed, alpha) / 255.0f,
                Premultiply(outGreen, alpha) / 255.0f,
                Premultiply(outBlue, alpha) / 255.0f,
                alpha / 255.0f);
        }
        """;
}
