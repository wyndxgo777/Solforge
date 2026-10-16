using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace SunriseStudio.Rendering;

/** Inputs of one deferred_shading draw; textures are views of the current frame. */
public sealed record NativeDeferredShadingInputs(NativeGBufferInputs GBuffer, GpuTexture? Diffuse, GpuTexture? Specular,
    GpuTexture Ssao, GpuTexture AtmosphereLookup, GpuTexture ScreenLookup, NativeCameraScopeConstants Camera,
    Matrix4x4 View, Matrix4x4 Projection, double SimulationSeconds, float ExposureEv,
    IReadOnlyList<Vector4> AtmosphereSlots, float InscatterScale, GpuTexture? Ibl = null,
    GpuTexture? FrameTexture24 = null, float TimeOfDay = NativeFrameInputs.ViewerTimeOfDay);

/** Technique 0, `deferred_shading`: lit G-buffer times atmosphere transmittance, plus in-scatter. */
public sealed class NativeDeferredShading : IDisposable
{
    public const uint MaterialTag = 0x80B35730;
    // Render_ShadingPass writes pass bytes 0x8181B381: blend 1, depth/stencil 51, rasterizer 1.
    public const uint PassStates = 0x8181B381;
    public const string Policy = "Deferred shading runs technique 0 (0x80B35730): the G-buffer lit by the lighting"
        + " buffers, times the atmosphere transmittance, plus the in-scatter from the 2D and screen lookups."
        + " Extern 3 resources 12 to 14 are read as lighting diffuse, specular and ibl specular (targets 128 to 130):"
        + " the shader multiplies them by diffuse colour, F0 and F0;"
        + " Environment lighting supplies IBL; extern 1 resource 24 reads the installed frame texture table."
        + " Depth/stencil 51 skips sky pixels by stencil; the viewer writes every pixel and redraws the sky over it."
        + " Frame row 1 X, which scales in-scatter and emission, carries the viewer's sky gain.";
    private const string VertexProgram = "3E020E44013D0705431F";
    private const string PixelProgram = "4C0049214C0149224C0249233F030747203F070747213F071147223F030A47233F030947243F030B"
        + "47253F030C47263F030D47273F030E47283F011847293F2A01472A3D070D43053C073C430A4E19432E3D011B22AA43383D0300"
        + "43394E7C22004E7D22000C460034003C073D3C0740043C07493C074A040C03432634013C07403C074A0C03432734023C073D3C07"
        + "490C034328340334043C073F3C073E02040C3C01040D3C073E3C073F3C073E02041D0E43293D07113C07482103432A34053C07"
        + "5402432B450022554500220002450022000C432F";
    // Extern 1 vector 27 is the constant (1, 1, 1, 1); Render_WriteFrameExtern1 0x7FF742EEC420.
    private static readonly Vector4 Extern1Vector27 = Vector4.One;

    private readonly Dx11Renderer renderer;
    private readonly List<IDisposable> owned = [];
    private readonly MaterialExecutionData execution;
    private readonly NativeMaterialPipeline pipeline;
    private readonly NativeDrawBindings bindings = new();
    private readonly NativeConstantBuffer vertexConstants, pixelConstants, vertexView, pixelView, frame;
    private readonly TfxParameterLookup parameters;
    private readonly GpuTexture zero;
    private readonly TfxResource depthToken = new("depth", ShaderResourceViewDimension.Texture2D),
        lookupToken = new("atmosphere 2D lookup", ShaderResourceViewDimension.Texture2D),
        screenToken = new("screen atmosphere lookup", ShaderResourceViewDimension.Texture2D),
        normalToken = new("gbuffer normal", ShaderResourceViewDimension.Texture2D),
        albedoToken = new("gbuffer albedo", ShaderResourceViewDimension.Texture2D),
        thirdToken = new("gbuffer third", ShaderResourceViewDimension.Texture2D),
        iblToken = new("lighting ibl specular", ShaderResourceViewDimension.Texture2D),
        specularToken = new("lighting specular", ShaderResourceViewDimension.Texture2D),
        diffuseToken = new("lighting diffuse", ShaderResourceViewDimension.Texture2D),
        extern1Token = new("extern 1 resource 24", ShaderResourceViewDimension.Texture2D),
        ssaoToken = new("ssao", ShaderResourceViewDimension.Texture2D);
    private bool disposed;

    /** The TFX programs are pinned; a changed program rejects the pass. */
    public NativeDeferredShading(Dx11Renderer renderer, GameMaterialReader reader, NativeRenderStateCatalog states,
        TfxParameterLookup parameters)
    {
        this.renderer = renderer;
        this.parameters = parameters;
        try
        {
            execution = reader.ReadExecution(MaterialTag);
            var material = execution.Material;
            if (material.VertexShader is null || material.PixelShader is null
                || Convert.ToHexString(execution.Vertex.TfxBytecode) != VertexProgram
                || Convert.ToHexString(execution.Pixel.TfxBytecode) != PixelProgram)
                throw new NotSupportedException("Deferred shading TFX programs differ from their verified contract.");
            var resolved = states.Resolve(PassStates, material.StateOverrides, 0, NativeDepthTable.Alternate);
            // The viewer has no sky stencil mark, so the draw covers every pixel without a depth or stencil test.
            var depth = resolved.DepthStencil;
            depth.DepthEnable = false; depth.StencilEnable = false; depth.DepthWriteMask = DepthWriteMask.Zero;
            var rasterizer = resolved.Rasterizer;
            rasterizer.DepthClipEnable = false;
            pipeline = Own(renderer.CreateNativePipeline(reader, material, [], rasterizer, depth, resolved.Blend));
            vertexConstants = Own(renderer.CreateNativeConstantBuffer(new byte[execution.Vertex.ConstantTemplate.Length * 16]));
            pixelConstants = Own(renderer.CreateNativeConstantBuffer(new byte[execution.Pixel.ConstantTemplate.Length * 16]));
            vertexView = Own(renderer.CreateNativeConstantBuffer(new byte[10 * 16]));
            pixelView = Own(renderer.CreateNativeConstantBuffer(new byte[13 * 16]));
            var initial = Frame(0, 0, 1);
            frame = Own(renderer.CreateNativeConstantBuffer(MemoryMarshal.AsBytes(initial.Vectors.AsSpan()), initial.KnownWordMasks));
            zero = Own(renderer.CreateTexture(1, 1, new byte[4]));
            var point = Own(renderer.Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp)));
            bindings.ConstantBuffers[new(GameShaderStage.Vertex, 0)] = vertexConstants;
            bindings.ConstantBuffers[new(GameShaderStage.Pixel, 0)] = pixelConstants;
            bindings.ConstantBuffers[new(GameShaderStage.Vertex, 12)] = vertexView;
            bindings.ConstantBuffers[new(GameShaderStage.Pixel, 12)] = pixelView;
            bindings.ConstantBuffers[new(GameShaderStage.Pixel, 13)] = frame;
            // TFX binds s1 to s3; s0 reads the full-resolution G-buffer at pixel centres.
            bindings.Samplers[new(GameShaderStage.Pixel, 0)] = point;
        }
        catch { Dispose(); throw; }
    }

    private T Own<T>(T item) where T : IDisposable { owned.Add(item); return item; }

    private static NativeFrameScopeConstants Frame(double seconds, float ev, float scale) => NativeFrameScope.Create(new()
    {
        SimulationSeconds = seconds, FrameFlag425B8 = false, FrameFlag425B9 = false,
        Extern1C = MathF.Pow(2, ev) * scale,
        Extern28 = MathF.Pow(2, BitConverter.UInt32BitsToSingle(0x3EA8F5C3) * ev) / MathF.Pow(2, ev)
    });

    /** Writes every pixel of target; draw the sky over its sky pixels afterwards. */
    public void Render(NativeDeferredShadingInputs inputs, NativeRenderTargets target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(inputs);
        var tfx = NativeFrameInputs.Create(inputs.SimulationSeconds, timeOfDay: inputs.TimeOfDay);
        tfx.Parameters = parameters;
        var camera = NativeViewInputs.CreateCameraInputs(checked((ushort)target.Width), checked((ushort)target.Height),
            inputs.View, inputs.Projection);
        foreach (var item in camera.Matrices) tfx.Matrices[item.Key] = item.Value;
        tfx.Matrices[new(2, 14)] = NativeAtmosphere.InvertPrecise(inputs.Projection);
        NativeAtmosphere.ApplyParameters(tfx, inputs.AtmosphereSlots, NativeAtmosphere.CameraHeight(inputs.View));
        tfx.Vectors[new(7, 5)] = NativeAtmosphere.SizeOf(inputs.ScreenLookup.Width, inputs.ScreenLookup.Height);
        tfx.Vectors[new(1, 27)] = Extern1Vector27;
        // Extern 3 vector 0: view depth = 1 / (x + depth * y) for forward depth.
        var p = inputs.Projection;
        tfx.Vectors[new(3, 0)] = new(-p.M33 / (p.M34 * p.M43), 1 / p.M43, 0, 0);
        tfx.Resources[new(3, 7)] = depthToken; tfx.Resources[new(7, 7)] = lookupToken; tfx.Resources[new(7, 17)] = screenToken;
        tfx.Resources[new(3, 10)] = normalToken; tfx.Resources[new(3, 9)] = albedoToken; tfx.Resources[new(3, 11)] = thirdToken;
        tfx.Resources[new(3, 12)] = diffuseToken; tfx.Resources[new(3, 13)] = specularToken; tfx.Resources[new(3, 14)] = iblToken;
        tfx.Resources[new(1, 24)] = extern1Token; tfx.Resources[new(42, 1)] = ssaoToken;
        var vertex = TfxEvaluator.Evaluate(execution.Vertex, tfx);
        var pixel = TfxEvaluator.Evaluate(execution.Pixel, tfx);
        vertex.RequireComplete(); pixel.RequireComplete();
        renderer.UpdateNativeConstantBuffer(vertexConstants, MemoryMarshal.AsBytes(vertex.Constants.AsSpan()));
        renderer.UpdateNativeConstantBuffer(pixelConstants, MemoryMarshal.AsBytes(pixel.Constants.AsSpan()));
        bindings.MaterialEvaluations[GameShaderStage.Vertex] = vertex;
        bindings.MaterialEvaluations[GameShaderStage.Pixel] = pixel;
        foreach (var sampler in pixel.Samplers)
            if (!bindings.Samplers.ContainsKey(sampler.Key))
                bindings.Samplers[sampler.Key] = Own(renderer.Device.CreateSamplerState(sampler.Value.Description));
        bindings.ApplyTfxResources(pixel, new Dictionary<TfxResource, ID3D11ShaderResourceView>
        {
            [depthToken] = inputs.GBuffer.Depth, [lookupToken] = inputs.AtmosphereLookup.View,
            [screenToken] = inputs.ScreenLookup.View, [normalToken] = inputs.GBuffer.Normal.View,
            [albedoToken] = inputs.GBuffer.Albedo.View, [thirdToken] = inputs.GBuffer.Third.View,
            [iblToken] = (inputs.Ibl ?? zero).View, [specularToken] = (inputs.Specular ?? zero).View,
            [diffuseToken] = (inputs.Diffuse ?? zero).View, [extern1Token] = (inputs.FrameTexture24 ?? zero).View,
            [ssaoToken] = inputs.Ssao.View
        });
        var vertexRows = new Vector4[10];
        Array.Copy(inputs.Camera.Vertex, vertexRows, 9);
        // Row 9 X is the quad depth; at the direction depth the VS outputs a pure world direction.
        vertexRows[9] = new(NativeAtmosphere.DirectionDepth(inputs.Projection), 0, 0, 0);
        var pixelRows = (Vector4[])inputs.Camera.Pixel.Clone();
        pixelRows[12] = NativeAtmosphere.SizeOf(target.Width, target.Height);
        renderer.UpdateNativeConstantBuffer(vertexView, MemoryMarshal.AsBytes(vertexRows.AsSpan()));
        renderer.UpdateNativeConstantBuffer(pixelView, MemoryMarshal.AsBytes(pixelRows.AsSpan()));
        var constants = Frame(inputs.SimulationSeconds, inputs.ExposureEv, inputs.InscatterScale);
        renderer.UpdateNativeConstantBuffer(frame, MemoryMarshal.AsBytes(constants.Vectors.AsSpan()));
        try { renderer.DrawNativeVertices(pipeline, bindings, target, PrimitiveTopology.TriangleStrip, 4); }
        finally
        {
            for (uint slot = 0; slot < 11; slot++) renderer.Context.PSSetShaderResource(slot, null!);
            renderer.RestoreMainTarget();
        }
    }

    /** Releases the GPU resources this pass owns. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        owned.Clear();
    }
}
