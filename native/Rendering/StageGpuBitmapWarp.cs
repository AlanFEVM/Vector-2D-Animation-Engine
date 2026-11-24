using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Size = System.Drawing.Size;
using Blend = Vortice.Direct3D11.Blend;
using BlendDescription = Vortice.Direct3D11.BlendDescription;
using FillMode = Vortice.Direct3D11.FillMode;
using Filter = Vortice.Direct3D11.Filter;

namespace VectorAnimationEngine;

// Rasterize the complete, ordered triangle mesh in one GPU draw. Shared edges
// use the hardware top-left rule; premultiplied blending also retains real folds.
internal sealed class StageGpuBitmapWarp : IDisposable
{
    private static readonly Lazy<Task<(byte[] Vertex, byte[] Pixel)>> ShaderBytecode = new(() => Task.Run(() =>
        (StageGpuOpticalShader.CompileComputeShader(VertexSource, "vs_5_0"),
         StageGpuOpticalShader.CompileComputeShader(PixelSource, "ps_5_0"))));

    internal static void StartShaderCompilation() => _ = ShaderBytecode.Value;

    private readonly StageGpuDevice _host;
    private readonly ID3D11DeviceContext _drawContext;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11DepthStencilState _depthDisabled;
    private readonly ID3D11SamplerState _linear;
    private readonly ID3D11SamplerState _point;
    private ID3D11Buffer? _vertices;
    private ID3D11ShaderResourceView? _vertexView;
    private int _capacity;
    private ID3D11Texture2D? _source;
    private ID3D11ShaderResourceView? _sourceView;
    private BitmapImageRasterKey? _sourceKey;
    private ID3D11Texture2D? _output;
    private ID3D11Texture2D? _multisample;
    private ID3D11RenderTargetView? _renderTarget;
    private ID2D1Bitmap1? _bitmap;
    private Size _size;
    private bool _primed;

    internal StageGpuBitmapWarp(StageGpuDevice host)
    {
        _host = host;
        var device = host.Device;
        try
        {
        var shader = ShaderBytecode.Value.GetAwaiter().GetResult();
        _drawContext = device.CreateDeferredContext();
        _vertexShader = device.CreateVertexShader(shader.Vertex);
        _pixelShader = device.CreatePixelShader(shader.Pixel);
        _blend = device.CreateBlendState(new BlendDescription(Blend.One, Blend.InverseSourceAlpha));
        _depthDisabled = device.CreateDepthStencilState(DepthStencilDescription.None);
        _rasterizer = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true,
            MultisampleEnable = true
        });
        _linear = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        _point = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
        }
        catch { Dispose(); throw; }
    }

    internal void Prime(BitmapImageRaster source, Size size)
    {
        Prepare(source, size);
        if (_primed) return;
        // Exercise the driver pipeline before the first pointer frame. This
        // tiny offscreen quad is never composited into the Stage.
        Vector4[] quad = [new(1, 0, 0, 0), new(-1, 1, 0, 0), new(-.99f, 1, 1, 0),
            new(-1, .99f, 0, 1), new(-.99f, .99f, 1, 1)];
        Draw(source, quad, 6, size, nearest: false, flushD2D: false);
    }

    private void Prepare(BitmapImageRaster source, Size size)
    {
        var device = _host.Device;
        if (_size != size)
        {
            ClearOutput();
            _size = size;
            var description = new Texture2DDescription(Format.B8G8R8A8_UNorm,
                (uint)size.Width, (uint)size.Height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource);
            _output = device.CreateTexture2D(description);
            using var surface = _output.QueryInterface<IDXGISurface>();
            _bitmap = _host.Context.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96, 96, BitmapOptions.None));
            description.SampleDescription = new SampleDescription(4, 0);
            _multisample = device.CreateTexture2D(description);
            _renderTarget = device.CreateRenderTargetView(_multisample);
        }
        if (_sourceKey != source.Key)
        {
            _sourceView?.Dispose(); _sourceView = null;
            _source?.Dispose(); _source = null;
            _sourceKey = null;
            _source = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,
                (uint)source.PixelWidth, (uint)source.PixelHeight, 1, 1, BindFlags.ShaderResource));
            _host.ImmediateContext.UpdateSubresource(source.Pixels, _source, 0, (uint)source.Stride, 0, null);
            _sourceView = device.CreateShaderResourceView(_source);
            _sourceKey = source.Key;
        }
    }

    internal ID2D1Bitmap Draw(BitmapImageRaster source, Vector4[] vertices, int drawVertexCount, Size size,
        bool nearest, bool flushD2D = true)
    {
        var device = _host.Device;
        var context = _drawContext;
        // Submit pending D2D work before overwriting the shared output.
        if (flushD2D) _host.Context.Flush(out _, out _);
        Prepare(source, size);
        if (_capacity != vertices.Length)
        {
            _vertexView?.Dispose(); _vertexView = null;
            _vertices?.Dispose(); _vertices = null;
            _capacity = vertices.Length;
            _vertices = device.CreateBuffer(vertices, BindFlags.ShaderResource, ResourceUsage.Default,
                CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, structureByteStride: 16);
            _vertexView = device.CreateShaderResourceView(_vertices);
        }
        else _host.ImmediateContext.UpdateSubresource(vertices, _vertices!, 0, 0, 0, null);
        context.ClearRenderTargetView(_renderTarget!, new Color4(0, 0, 0, 0));
        context.OMSetRenderTargets(_renderTarget!);
        context.OMSetBlendState(_blend);
        context.OMSetDepthStencilState(_depthDisabled);
        context.RSSetState(_rasterizer);
        context.RSSetViewport(new Viewport(0, 0, size.Width, size.Height));
        context.IASetInputLayout(null!);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.VSSetShaderResource(0, _vertexView!);
        context.PSSetShader(_pixelShader);
        context.PSSetShaderResource(0, _sourceView!);
        context.PSSetSampler(0, nearest ? _point : _linear);
        try { context.Draw((uint)drawVertexCount, 0); }
        finally
        {
            context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
            context.VSSetShaderResource(0, null!);
            context.PSSetShaderResource(0, null!);
            context.PSSetSampler(0, null!);
            context.VSSetShader(null!);
            context.PSSetShader(null!);
        }
        context.ResolveSubresource(_output!, 0, _multisample!, 0, Format.B8G8R8A8_UNorm);
        using var commands = context.FinishCommandList(false);
        // D2D caches graphics pipeline state on this same immediate context.
        // Restore it after the mesh draw rather than corrupting subsequent passes.
        _host.ImmediateContext.ExecuteCommandList(commands, true);
        _primed = true;
        return _bitmap!;
    }

    private void ClearOutput()
    {
        _bitmap?.Dispose(); _bitmap = null;
        _renderTarget?.Dispose(); _renderTarget = null;
        _multisample?.Dispose(); _multisample = null;
        _output?.Dispose(); _output = null;
        _size = default;
    }

    public void Dispose()
    {
        ClearOutput();
        _sourceView?.Dispose(); _source?.Dispose();
        _vertexView?.Dispose(); _vertices?.Dispose();
        _point?.Dispose(); _linear?.Dispose(); _rasterizer?.Dispose(); _blend?.Dispose(); _depthDisabled?.Dispose();
        _pixelShader?.Dispose(); _vertexShader?.Dispose(); _drawContext?.Dispose();
    }

    private const string VertexSource = """
        StructuredBuffer<float4> vertices : register(t0);
        struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
        Vertex main(uint id : SV_VertexID) {
            uint columns = (uint)vertices[0].x;
            uint cell = id / 6;
            uint corner = id % 6;
            uint x = cell % columns + (corner == 1 || corner == 3 || corner == 4 ? 1 : 0);
            uint y = cell / columns + (corner == 2 || corner >= 4 ? 1 : 0);
            float4 v = vertices[1 + y * (columns + 1) + x]; Vertex o;
            o.position = float4(v.xy, 0, 1); o.uv = v.zw; return o;
        }
        """;
    private const string PixelSource = """
        Texture2D source : register(t0); SamplerState sampling : register(s0);
        float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
            return source.SampleLevel(sampling, uv, 0);
        }
        """;
}
