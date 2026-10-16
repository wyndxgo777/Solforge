using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace SunriseStudio.Rendering;

/** One camera-facing particle; Size is the world half-width and Spin.x the screen rotation in radians. */
[StructLayout(LayoutKind.Sequential)]
public struct EffectBillboard(Vector3 position, float size, Vector4 color, float spin)
{
    public Vector3 Position = position;
    public float Size = size;
    public Vector4 Color = color;
    public Vector2 Spin = new(spin, 0);
    public Vector2 Padding;
}

/** A run of uploaded billboards that share one visual. */
public readonly record struct EffectDrawRange(int Visual, int First, int Count);

/** Instanced viewer billboards for effects; depth-tested against the scene and never written to depth. */
public sealed unsafe class EffectRenderer : IDisposable
{
    private sealed record Visual(GpuTexture? Texture, bool SingleChannel, bool Premultiply, ID3D11BlendState Blend);

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public Matrix4x4 ViewProjection;
        public Vector4 CameraRight;
        public Vector4 CameraUp;
        // x intensity, y single-channel mask, z premultiply by alpha.
        public Vector4 Parameters;
    }

    private readonly Dx11Renderer renderer;
    private readonly ID3D11VertexShader vertexShader;
    private readonly ID3D11PixelShader pixelShader;
    private readonly ID3D11InputLayout layout;
    private readonly ID3D11Buffer constants;
    private readonly ID3D11SamplerState sampler;
    private readonly ID3D11DepthStencilState depthRead;
    private readonly ID3D11RasterizerState rasterizer;
    private readonly ID3D11BlendState additive;
    private readonly GpuTexture white;
    private readonly List<Visual> visuals = [];
    private readonly Dictionary<int, ID3D11BlendState> authoredBlends = [];
    private ID3D11Buffer? instances;
    private int capacity, uploaded;
    private bool disposed;

    public int VisualCount => visuals.Count;

    public EffectRenderer(Dx11Renderer renderer)
    {
        this.renderer = renderer;
        var device = renderer.Device;
        var vs = Dx11Renderer.Compile(Shader, "VSMain", "vs_5_0");
        vertexShader = device.CreateVertexShader(vs.Span);
        pixelShader = device.CreatePixelShader(Dx11Renderer.Compile(Shader, "PSMain", "ps_5_0").Span);
        layout = device.CreateInputLayout(
        [
            new("POSITION", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerInstanceData, 1),
            new("SIZE", 0, Format.R32_Float, 12, 0, InputClassification.PerInstanceData, 1),
            new("COLOR", 0, Format.R32G32B32A32_Float, 16, 0, InputClassification.PerInstanceData, 1),
            new("SPIN", 0, Format.R32G32_Float, 32, 0, InputClassification.PerInstanceData, 1)
        ], vs.Span);
        constants = device.CreateBuffer((uint)sizeof(Constants), BindFlags.ConstantBuffer);
        sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        depthRead = device.CreateDepthStencilState(new DepthStencilDescription(true, DepthWriteMask.Zero, ComparisonFunction.LessEqual));
        rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        var blend = new BlendDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.One, DestinationBlend = Blend.One, BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.Zero, DestinationBlendAlpha = Blend.One, BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        additive = device.CreateBlendState(blend);
        white = renderer.CreateTexture(1, 1, [255, 255, 255, 255]);
    }

    /**
     * Registers a texture and blend for later draws; the effect renderer does not own the texture.
     * @param authoredBlend Native blend-state index, or null for premultiplied additive blending.
     * @return The visual index used by EffectDrawRange.
     */
    public int AddVisual(GpuTexture? texture, bool singleChannel, int? authoredBlend, NativeRenderStateCatalog? states)
    {
        var state = additive;
        bool premultiply = true;
        if (authoredBlend is { } index && states is not null && index < states.BlendCount)
        {
            var description = states.GetBlend(index);
            if (!authoredBlends.TryGetValue(index, out state!))
                authoredBlends[index] = state = renderer.Device.CreateBlendState(description);
            premultiply = description.RenderTarget[0].SourceBlend == Blend.One;
        }
        visuals.Add(new Visual(texture, singleChannel, premultiply, state));
        return visuals.Count - 1;
    }

    /** Copies this frame's billboards into the dynamic instance buffer, growing it when needed. */
    public void Upload(ReadOnlySpan<EffectBillboard> billboards)
    {
        uploaded = billboards.Length;
        if (billboards.IsEmpty) return;
        if (capacity < billboards.Length)
        {
            instances?.Dispose();
            capacity = Math.Max(billboards.Length * 3 / 2, 4096);
            instances = renderer.Device.CreateBuffer((uint)(capacity * sizeof(EffectBillboard)),
                BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        }
        var mapped = renderer.Context.Map(instances!, 0, MapMode.WriteDiscard, MapFlags.None);
        billboards.CopyTo(new Span<EffectBillboard>((void*)mapped.DataPointer, billboards.Length));
        renderer.Context.Unmap(instances!, 0);
    }

    /** Draws into the swap chain with the main depth buffer. */
    public void DrawMain(Matrix4x4 view, Matrix4x4 viewProjection, IReadOnlyList<EffectDrawRange> ranges, float intensity)
    {
        renderer.RestoreMainTarget();
        Draw(view, viewProjection, ranges, intensity);
    }

    /** Draws into the first color target of a native target set, testing against its depth. */
    public void DrawNative(NativeRenderTargets targets, Matrix4x4 view, Matrix4x4 viewProjection,
        IReadOnlyList<EffectDrawRange> ranges, float intensity)
    {
        var context = renderer.Context;
        context.OMSetRenderTargets(targets.Views[0], targets.DepthView);
        context.RSSetViewport(new Vortice.Mathematics.Viewport(targets.Width, targets.Height));
        context.RSSetScissorRect(targets.Width, targets.Height);
        Draw(view, viewProjection, ranges, intensity);
    }

    private void Draw(Matrix4x4 view, Matrix4x4 viewProjection, IReadOnlyList<EffectDrawRange> ranges, float intensity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (instances is null || uploaded == 0 || ranges.Count == 0) return;
        Matrix4x4.Invert(view, out var inverse);
        var data = new Constants
        {
            ViewProjection = viewProjection,
            CameraRight = new(inverse.M11, inverse.M12, inverse.M13, 0),
            CameraUp = new(inverse.M21, inverse.M22, inverse.M23, 0)
        };
        var context = renderer.Context;
        context.IASetInputLayout(layout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.IASetVertexBuffer(0, instances, (uint)sizeof(EffectBillboard));
        context.VSSetShader(vertexShader);
        context.VSSetConstantBuffer(0, constants);
        context.PSSetShader(pixelShader);
        context.PSSetConstantBuffer(0, constants);
        context.PSSetSampler(0, sampler);
        context.RSSetState(rasterizer);
        context.OMSetDepthStencilState(depthRead);
        int draws = 0;
        foreach (var range in ranges)
        {
            if (range.Count <= 0 || (uint)range.Visual >= visuals.Count || range.First + range.Count > uploaded) continue;
            var visual = visuals[range.Visual];
            data.Parameters = new(intensity, visual.SingleChannel ? 1 : 0, visual.Premultiply ? 1 : 0, 0);
            context.UpdateSubresource(in data, constants);
            context.PSSetShaderResource(0, (visual.Texture ?? white).View);
            context.OMSetBlendState(visual.Blend);
            context.DrawInstanced(4, (uint)range.Count, 0, (uint)range.First);
            draws++;
        }
        context.OMSetBlendState(null);
        renderer.CountDraws(draws);
    }

    /** Releases the GPU resources this renderer owns. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        instances?.Dispose();
        foreach (var state in authoredBlends.Values) state.Dispose();
        additive.Dispose(); rasterizer.Dispose(); depthRead.Dispose(); sampler.Dispose();
        constants.Dispose(); layout.Dispose(); pixelShader.Dispose(); vertexShader.Dispose();
        white.Dispose();
    }

    private const string Shader = """
        cbuffer EffectData : register(b0) {
            row_major float4x4 ViewProjection;
            float4 CameraRight;
            float4 CameraUp;
            float4 Parameters;
        };
        Texture2D Sprite : register(t0);
        SamplerState LinearSampler : register(s0);
        struct Particle { float3 Position : POSITION; float Size : SIZE; float4 Color : COLOR0; float2 Spin : SPIN; };
        struct PSInput { float4 Position : SV_POSITION; float2 UV : TEXCOORD0; float4 Color : COLOR0; };
        PSInput VSMain(uint vertex : SV_VertexID, Particle particle) {
            float2 corner = float2((vertex & 1) ? 1 : -1, (vertex & 2) ? 1 : -1);
            float s, c;
            sincos(particle.Spin.x, s, c);
            float2 turned = float2(corner.x * c - corner.y * s, corner.x * s + corner.y * c);
            float3 world = particle.Position + (CameraRight.xyz * turned.x + CameraUp.xyz * turned.y) * particle.Size;
            PSInput output;
            output.Position = mul(float4(world, 1), ViewProjection);
            output.UV = float2(corner.x * 0.5 + 0.5, 0.5 - corner.y * 0.5);
            output.Color = particle.Color;
            return output;
        }
        float4 PSMain(PSInput input) : SV_TARGET {
            float4 texel = Sprite.Sample(LinearSampler, input.UV);
            if (Parameters.y > 0.5) texel = float4(1, 1, 1, texel.r);
            // A radial falloff hides sprite edges that the game's own pixel shader would shape.
            float2 centered = input.UV * 2 - 1;
            texel.a *= saturate(1 - dot(centered, centered));
            float4 color = texel * input.Color;
            color.rgb *= Parameters.x;
            if (Parameters.z > 0.5) color.rgb *= color.a;
            return color;
        }
        """;
}
