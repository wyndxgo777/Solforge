using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/**
 * One cubemap draw: its binding plus the cubemap texture it samples. This file also holds the
 * cubemap-lighting pass.
 */
public sealed record NativeCubemapDraw(NativeCubemapBinding Binding, GpuTexture Texture);

/** Native volume shaders replace sky lighting only inside their authored boxes and fades. */
public sealed class NativeCubemapLighting : IDisposable
{
    private const uint StencilMaterial = 0x80B3583F;
    private const string PixelProgram = "4C0049214C0149224C0249233F030A47233F030747243F570047253D030043004E52220043024E7F2200430642064E8022000343074E7C22004E7D22000C4600450022554500220002450022000C4301";
    private static readonly Vector3[] Vertices = [Vector3.Zero, new(-1, -1, -1), new(-1, 1, -1),
        new(1, 1, -1), new(1, -1, -1), new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)];
    private static readonly ushort[] Indices = [2, 1, 3, 4, 3, 1, 6, 5, 7, 8, 7, 5, 4, 1, 6, 5, 6, 1,
        3, 4, 7, 6, 7, 4, 2, 3, 8, 7, 8, 3, 1, 2, 5, 8, 5, 2];
    private readonly Dx11Renderer renderer;
    private readonly List<IDisposable> owned = [];
    private readonly MaterialExecutionData execution, stencilExecution;
    private readonly NativeMaterialPipeline inside, outside;
    private readonly NativeDrawBindings bindings = new() { StencilReference = 0 };
    private readonly NativeGpuMesh mesh;
    private readonly NativeConstantBuffer materialConstants, vertexVolume, pixelVolume, vertexView, pixelView, frame;
    private readonly NativeConstantBuffer stencilConstants;
    private readonly ID3D11VertexShader stencilShader;
    private readonly ID3D11InputLayout stencilLayout;
    private readonly ID3D11BlendState stencilBlend;
    private readonly ID3D11RasterizerState stencilRasterizer;
    private readonly ID3D11DepthStencilState stencilInside, stencilOutside;
    private readonly TfxParameterLookup parameters;
    private readonly TfxResource normalToken = new("gbuffer normal", ShaderResourceViewDimension.Texture2D),
        depthToken = new("depth", ShaderResourceViewDimension.Texture2D),
        aoToken = new("temporary AO", ShaderResourceViewDimension.Texture2D);
    private NativeRenderTargets? targets;
    private bool disposed;
    public GpuTexture? Diffuse => targets?.Textures[0];
    public GpuTexture? Ibl => targets?.Textures[1];
    public int LastDrawCount { get; private set; }

    public NativeCubemapLighting(Dx11Renderer renderer, GameMaterialReader reader,
        NativeRenderStateCatalog states, TfxParameterLookup parameters)
    {
        this.renderer = renderer; this.parameters = parameters;
        try
        {
            execution = reader.ReadExecution(NativeCubemapBinding.MaterialTag);
            stencilExecution = reader.ReadExecution(StencilMaterial);
            if (execution.Vertex.TfxBytecode.Length != 0 || Convert.ToHexString(execution.Pixel.TfxBytecode) != PixelProgram
                || Convert.ToHexString(stencilExecution.Vertex.TfxBytecode) != "3E0F004400"
                || stencilExecution.Material.PixelShader is not null)
                throw new NotSupportedException("Cubemap volume programs changed.");
            InputElementDescription[] elements = [new("POSITION", 0, Format.R32G32B32_Float, 0, 0)];
            NativeMaterialPipeline Pipeline(uint forced)
            {
                var state = states.Resolve(0x81819081, execution.Material.StateOverrides, forced, NativeDepthTable.Alternate);
                return Own(renderer.CreateNativePipeline(reader, execution.Material, elements,
                    state.Rasterizer, state.DepthStencil, state.Blend));
            }
            inside = Pipeline(0x0083D197); outside = Pipeline(0x0082D197);
            mesh = Own(renderer.CreateNativeMesh([new(MemoryMarshal.AsBytes(Vertices.AsSpan()).ToArray(), 12)],
                MemoryMarshal.AsBytes(Indices.AsSpan()), Format.R16_UInt, 0, 36, PrimitiveTopology.TriangleList));
            materialConstants = Buffer(8); vertexVolume = Buffer(3);
            var masks = Enumerable.Range(0, 18 * 4).ToDictionary(i => (uint)i, _ => 0u);
            foreach (int row in new[] { 0, 1, 2, 3, 5, 6, 7, 8 })
                for (int component = 0; component < 4; component++) masks[(uint)(row * 4 + component)] = uint.MaxValue;
            foreach (int word in new[] { 13 * 4 + 3, 14 * 4 + 2, 17 * 4 + 3 }) masks[(uint)word] = uint.MaxValue;
            pixelVolume = Own(renderer.CreateNativeConstantBuffer(new byte[18 * 16], masks));
            vertexView = Buffer(14); pixelView = Buffer(13);
            var initial = NativeFrameScope.Create(new() { SimulationSeconds = 0, Extern1C = 1 });
            frame = Own(renderer.CreateNativeConstantBuffer(MemoryMarshal.AsBytes(initial.Vectors.AsSpan()), initial.KnownWordMasks));
            stencilConstants = Buffer(4);
            var stencilCode = reader.ReadShaderBytecode(stencilExecution.Material.VertexShader!);
            stencilShader = Own(renderer.Device.CreateVertexShader(stencilCode));
            stencilLayout = Own(renderer.Device.CreateInputLayout(elements, stencilCode));
            var preInside = states.Resolve(0x81819081, stencilExecution.Material.StateOverrides, 0x008198CE, NativeDepthTable.Alternate);
            var preOutside = states.Resolve(0x81819081, stencilExecution.Material.StateOverrides, 0x00819DCE, NativeDepthTable.Alternate);
            stencilBlend = Own(renderer.Device.CreateBlendState(preInside.Blend));
            stencilRasterizer = Own(renderer.Device.CreateRasterizerState(preInside.Rasterizer));
            stencilInside = Own(renderer.Device.CreateDepthStencilState(preInside.DepthStencil));
            stencilOutside = Own(renderer.Device.CreateDepthStencilState(preOutside.DepthStencil));
            bindings.ConstantBuffers[new(GameShaderStage.Pixel, 0)] = materialConstants;
            bindings.ConstantBuffers[new(GameShaderStage.Vertex, 11)] = vertexVolume;
            bindings.ConstantBuffers[new(GameShaderStage.Pixel, 11)] = pixelVolume;
            bindings.ConstantBuffers[new(GameShaderStage.Vertex, 12)] = vertexView;
            bindings.ConstantBuffers[new(GameShaderStage.Pixel, 12)] = pixelView;
            bindings.ConstantBuffers[new(GameShaderStage.Pixel, 13)] = frame;
        }
        catch { Dispose(); throw; }
    }

    private T Own<T>(T item) where T : IDisposable { owned.Add(item); return item; }
    private NativeConstantBuffer Buffer(int rows) => Own(renderer.CreateNativeConstantBuffer(new byte[rows * 16]));

    /** The stencil prepass and lighting draw share the G-buffer's depth and stencil planes. */
    public void Render(IReadOnlyList<NativeCubemapDraw> volumes, NativeGBufferInputs inputs,
        NativeRenderTargets depthOwner, GpuTexture skyDiffuse, GpuTexture skyIbl, GpuTexture temporaryAo,
        NativeCameraScopeConstants camera, Matrix4x4 view, Matrix4x4 projection, double seconds, float ev, float nearPlane)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!depthOwner.HasStencil) throw new ArgumentException("Cubemap volumes need the native stencil plane.");
        if (targets is null || !ReferenceEquals(targets.Depth, depthOwner.Depth))
        {
            targets?.Dispose();
            targets = renderer.CreateNativeTargetsOnDepth([new(Format.R11G11B10_Float, Format.R11G11B10_Float, Format.R11G11B10_Float),
                new(Format.R11G11B10_Float, Format.R11G11B10_Float, Format.R11G11B10_Float)], depthOwner);
        }
        renderer.CopyTexture(targets.Textures[0], skyDiffuse); renderer.CopyTexture(targets.Textures[1], skyIbl);
        renderer.UpdateNativeConstantBuffer(vertexView, MemoryMarshal.AsBytes(camera.Vertex.AsSpan()));
        renderer.UpdateNativeConstantBuffer(pixelView, MemoryMarshal.AsBytes(camera.Pixel.AsSpan()));
        var constants = NativeFrameScope.Create(new() { SimulationSeconds = seconds, Extern1C = MathF.Pow(2, ev) });
        renderer.UpdateNativeConstantBuffer(frame, MemoryMarshal.AsBytes(constants.Vectors.AsSpan()));
        var tfx = new TfxInputs { Parameters = parameters };
        tfx.Vectors[new(3, 0)] = new(-projection.M33 / (projection.M34 * projection.M43), 1 / projection.M43, 0, 0);
        tfx.Resources[new(3, 10)] = normalToken; tfx.Resources[new(3, 7)] = depthToken; tfx.Resources[new(87, 0)] = aoToken;
        var pixel = TfxEvaluator.Evaluate(execution.Pixel, tfx); pixel.RequireComplete();
        renderer.UpdateNativeConstantBuffer(materialConstants, MemoryMarshal.AsBytes(pixel.Constants.AsSpan()));
        bindings.MaterialEvaluations[GameShaderStage.Pixel] = pixel;
        bindings.ApplyTfxResources(pixel, new Dictionary<TfxResource, ID3D11ShaderResourceView>
        { [normalToken] = inputs.Normal.View, [depthToken] = inputs.Depth, [aoToken] = temporaryAo.View });
        foreach (var sampler in pixel.Samplers)
            if (!bindings.Samplers.ContainsKey(sampler.Key))
                bindings.Samplers[sampler.Key] = Own(renderer.Device.CreateSamplerState(sampler.Value.Description));
        Matrix4x4.Invert(view, out var inverseView);
        LastDrawCount = 0;
        try
        {
            foreach (var volume in volumes)
            {
                if (volume.Texture.Dimension != ShaderResourceViewDimension.TextureCube)
                    throw new ArgumentException("Cubemap volume needs an authored cube texture.");
                var data = volume.Binding;
                bool within = data.Contains(inverseView.Translation, nearPlane);
                DrawStencil(data.StencilTransform * view * projection, within);
                renderer.UpdateNativeConstantBuffer(vertexVolume, MemoryMarshal.AsBytes(data.Vertex.AsSpan()));
                renderer.UpdateNativeConstantBuffer(pixelVolume, MemoryMarshal.AsBytes(data.Pixel.AsSpan()));
                bindings.Resources[new(GameShaderStage.Pixel, 0)] = volume.Texture.View;
                renderer.DrawNative(within ? inside : outside, mesh, bindings, targets);
                LastDrawCount++;
            }
        }
        finally
        {
            for (uint slot = 0; slot < 6; slot++) renderer.Context.PSSetShaderResource(slot, null!);
            renderer.RestoreMainTarget();
        }
    }

    /** Technique 92 has no pixel shader and writes the native volume stencil only. */
    private void DrawStencil(Matrix4x4 worldToClip, bool within)
    {
        var tfx = new TfxInputs(); tfx.Matrices[new(15, 0)] = worldToClip;
        var evaluation = TfxEvaluator.Evaluate(stencilExecution.Vertex, tfx); evaluation.RequireComplete();
        renderer.UpdateNativeConstantBuffer(stencilConstants, MemoryMarshal.AsBytes(evaluation.Constants.AsSpan()));
        var context = renderer.Context;
        context.OMSetRenderTargets(targets!.Views, targets.DepthView);
        context.RSSetViewport(new Vortice.Mathematics.Viewport(targets.Width, targets.Height));
        context.RSSetScissorRect(targets.Width, targets.Height);
        context.OMSetBlendState(stencilBlend);
        context.OMSetDepthStencilState(within ? stencilInside : stencilOutside, 16);
        context.RSSetState(stencilRasterizer);
        context.IASetInputLayout(stencilLayout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetVertexBuffer(0, mesh.Streams[0], 12);
        context.IASetIndexBuffer(mesh.Indices, Format.R16_UInt, 0);
        context.VSSetShader(stencilShader); context.PSSetShader(null);
        context.VSSetConstantBuffer(0, stencilConstants.Buffer);
        context.DrawIndexed(36, 0, 0); renderer.CountDraws(1);
    }

    /** Releases the GPU resources this pass owns. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        targets?.Dispose();
        for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        owned.Clear();
    }
}
