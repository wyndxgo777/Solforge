using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SunriseStudio.Rendering;

/** This inspection pass preserves source MRTs and forces display alpha to one. */
public sealed class NativeOutputDisplay : IDisposable
{
    private readonly Dx11Renderer renderer;
    private readonly ID3D11RenderTargetView targetView;
    private readonly ID3D11VertexShader vertexShader;
    private readonly ID3D11PixelShader pixelShader;
    private readonly ID3D11Buffer constants;
    private readonly ID3D11SamplerState sampler;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11DepthStencilState depth;
    private readonly ID3D11BlendState blend;
    private bool disposed;
    public GpuTexture Texture { get; }

    /** The display target has its own extent and never aliases the original attachment. */
    public NativeOutputDisplay(Dx11Renderer renderer, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        this.renderer = renderer;
        var device = renderer.Device;
        ID3D11Texture2D? resource = null;
        try
        {
            resource = device.CreateTexture2D(Format.R8G8B8A8_UNorm, (uint)width, (uint)height,
                mipLevels: 1, bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource);
            Texture = new(resource, device.CreateShaderResourceView(resource), width, height);
            resource = null;
            targetView = device.CreateRenderTargetView(Texture.Resource);
            vertexShader = device.CreateVertexShader(Dx11Renderer.Compile(Shader, "VSMain", "vs_5_0").Span);
            pixelShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSMain", "ps_5_0").Span);
            constants = device.CreateBuffer(16, BindFlags.ConstantBuffer);
            sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
            rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
            depth = device.CreateDepthStencilState(new DepthStencilDescription(false, DepthWriteMask.Zero,
                ComparisonFunction.Always));
            blend = device.CreateBlendState(new BlendDescription(Blend.One, Blend.Zero));
        }
        catch
        {
            resource?.Dispose(); blend?.Dispose(); depth?.Dispose(); rasterizer?.Dispose(); sampler?.Dispose();
            constants?.Dispose(); pixelShader?.Dispose(); vertexShader?.Dispose(); targetView?.Dispose();
            Texture?.Dispose();
            throw;
        }
    }

    /** Channels are RGB, R, G, B, A; exposure is measured in stops. */
    public GpuTexture Render(GpuTexture source, int channel = 0, bool srgb = false, float exposure = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (source.Dimension != ShaderResourceViewDimension.Texture2D)
            throw new NotSupportedException("Raw output display needs a two-dimensional source.");
        if (source.Resource.NativePointer == Texture.Resource.NativePointer)
            throw new ArgumentException("Display source cannot be its own output.", nameof(source));
        if (channel is < 0 or > 4)
            throw new ArgumentOutOfRangeException(nameof(channel));
        if (!float.IsFinite(exposure) || exposure is < -32 or > 32)
            throw new ArgumentOutOfRangeException(nameof(exposure));
        var data = new Vector4(channel, srgb ? 1 : 0, MathF.Pow(2, exposure), 0);
        var context = renderer.Context;
        try
        {
            context.PSSetShaderResource(0, null!);
            context.OMSetRenderTargets(targetView);
            context.RSSetViewport(new Viewport(Texture.Width, Texture.Height));
            context.RSSetScissorRect(Texture.Width, Texture.Height);
            context.RSSetState(rasterizer);
            context.OMSetDepthStencilState(depth);
            context.OMSetBlendState(blend);
            context.IASetInputLayout(null!);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.VSSetShader(vertexShader);
            context.PSSetShader(pixelShader);
            context.UpdateSubresource(in data, constants);
            context.PSSetConstantBuffer(0, constants);
            context.PSSetShaderResource(0, source.View);
            context.PSSetSampler(0, sampler);
            context.Draw(3, 0);
        }
        finally
        {
            context.PSSetShaderResource(0, null!);
            renderer.RestoreMainTarget();
        }
        return Texture;
    }

    /** Releases the GPU resources this pass owns. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        blend.Dispose(); depth.Dispose(); rasterizer.Dispose(); sampler.Dispose();
        constants.Dispose(); pixelShader.Dispose(); vertexShader.Dispose();
        targetView.Dispose(); Texture.Dispose();
    }

    private const string Shader = """
        cbuffer DisplaySettings : register(b0) { float4 Settings; };
        Texture2D Source : register(t0);
        SamplerState PointClamp : register(s0);
        struct PixelInput { float4 Position : SV_POSITION; float2 UV : TEXCOORD0; };
        PixelInput VSMain(uint id : SV_VertexID) {
            PixelInput result;
            result.UV = float2((id << 1) & 2, id & 2);
            result.Position = float4(result.UV * float2(2, -2) + float2(-1, 1), 0, 1);
            return result;
        }
        float4 PSMain(PixelInput input) : SV_TARGET {
            float4 sampled = Source.SampleLevel(PointClamp, input.UV, 0);
            int channel = (int)Settings.x;
            float3 value = sampled.rgb;
            if (channel > 0) value = sampled[channel - 1].xxx;
            value = max(value * Settings.z, 0);
            if (Settings.y != 0) {
                float3 low = value * 12.92;
                float3 high = 1.055 * pow(value, 1.0 / 2.4) - 0.055;
                value = float3(value.x <= 0.0031308 ? low.x : high.x,
                    value.y <= 0.0031308 ? low.y : high.y,
                    value.z <= 0.0031308 ? low.z : high.z);
            }
            return float4(saturate(value), 1);
        }
        """;
}
