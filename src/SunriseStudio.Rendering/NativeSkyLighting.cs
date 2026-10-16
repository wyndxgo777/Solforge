using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/** Atmospheric sky lighting uses installed techniques and the placed atmosphere's lookup textures. */
public sealed class NativeSkyLighting : IDisposable
{
    public const uint GenerateTag = 0x80B35F99, TintTag = 0x80B9E2AE, FilterTag = 0x80B9E2A3,
        ApplyTag = 0x80B9E24B;
    public const int ReflectionSize = 512, ReflectionMips = 10;
    public const string Policy = "Atmospheric reflection uses native techniques 418, 587, 585 and 568. "
        + "Placed atmosphere lookups supply the sky. Local reflection probes and sky-object captures are not included.";
    private sealed record Pass(MaterialExecutionData Execution, NativeMaterialPipeline Pipeline,
        NativeConstantBuffer? Vertex, NativeConstantBuffer? Pixel)
    {
        public NativeDrawBindings Bindings { get; } = new();
    }

    private readonly Dx11Renderer renderer;
    private readonly List<IDisposable> owned = [];
    private readonly Pass generate, tint, filter, apply;
    private readonly TfxParameterLookup parameters;
    private readonly NativeRenderStateCatalog states;
    private readonly Vector4[] slots;
    private readonly NativeConstantBuffer vertexView, pixelView, frameBuffer;
    private readonly NativeRenderTargets atmosphere, tinted;
    private readonly NativeRenderTargets[] filtered;
    private readonly GpuTexture reflection;
    private NativeRenderTargets? lighting;
    private GpuTexture? lastLookup;
    private GpuTexture? lastSecondLookup;
    private float lastKeyframe = float.NaN;
    private Vector3 lastLight;
    private bool disposed;
    private readonly TfxResource first = new("atmosphere first", ShaderResourceViewDimension.Texture3D),
        second = new("atmosphere second", ShaderResourceViewDimension.Texture3D),
        source = new("hemisphere source", ShaderResourceViewDimension.Texture2D),
        third = new("gbuffer third", ShaderResourceViewDimension.Texture2D),
        normal = new("gbuffer normal", ShaderResourceViewDimension.Texture2D),
        sky = new("sky reflection", ShaderResourceViewDimension.Texture2D);

    public GpuTexture Reflection => reflection;
    public GpuTexture? Diffuse => lighting?.Textures[0];
    public GpuTexture? Ibl => lighting?.Textures[1];
    public GpuTexture? TemporaryAo => lighting?.Textures[2];
    public int GenerationCount { get; private set; }

    public NativeSkyLighting(Dx11Renderer renderer, GameMaterialReader reader, TfxParameterDefaults defaults,
        TfxParameterLookup parameters, NativeRenderStateCatalog states)
    {
        this.renderer = renderer;
        this.parameters = parameters;
        this.states = states;
        try
        {
            slots = NativeAtmosphere.ReadSlots(defaults, parameters, out _);
            generate = CreatePass(reader, GenerateTag, "3E020E4400",
                "4C0049213F070447203F070547213D070D43043C073843053C073943063C075143163C075243173C070C431A3D070A431B3D071643204E2B432B");
            tint = CreatePass(reader, TintTag, "", "4C0049213F250047244E81221A43014E55221A43024E8222004303");
            filter = CreatePass(reader, FilterTag, "", "4C0049213F250047243D250B220043023D250B226A4303");
            apply = CreatePass(reader, ApplyTag, "",
                "4C0049213F030B47243F030A47253F031347264E52220043023E022244034E8722004E7F220003430742074E8022000343084E7C22004E7D22000C4600450022554500220002450022000C4301");
            vertexView = Own(renderer.CreateNativeConstantBuffer(new byte[10 * 16],
                new Dictionary<uint, uint> { [36] = uint.MaxValue, [37] = 0, [38] = 0, [39] = 0 }));
            pixelView = Own(renderer.CreateNativeConstantBuffer(new byte[13 * 16]));
            var initialFrame = NativeFrameScope.Create(new() { SimulationSeconds = 0,
                FrameFlag425B8 = false, FrameFlag425B9 = false, Extern1C = 1 });
            frameBuffer = Own(renderer.CreateNativeConstantBuffer(MemoryMarshal.AsBytes(initialFrame.Vectors.AsSpan()),
                initialFrame.KnownWordMasks));
            var point = Own(renderer.Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp)));
            foreach (var pass in new[] { generate, tint, filter, apply })
            {
                if (pass.Vertex is not null) pass.Bindings.ConstantBuffers[new(GameShaderStage.Vertex, 0)] = pass.Vertex;
                if (pass.Pixel is not null) pass.Bindings.ConstantBuffers[new(GameShaderStage.Pixel, 0)] = pass.Pixel;
                pass.Bindings.ConstantBuffers[new(GameShaderStage.Vertex, 12)] = vertexView;
                pass.Bindings.ConstantBuffers[new(GameShaderStage.Pixel, 12)] = pixelView;
                pass.Bindings.ConstantBuffers[new(GameShaderStage.Pixel, 13)] = frameBuffer;
                pass.Bindings.Samplers[new(GameShaderStage.Pixel, 0)] = point;
            }
            atmosphere = Own(renderer.CreateNativeTargets(64, 64, Format.R16G16B16A16_Float));
            tinted = Own(renderer.CreateNativeTargets(ReflectionSize, ReflectionSize, Format.R16G16B16A16_Float));
            filtered = Enumerable.Range(1, ReflectionMips - 1).Select(mip => Own(renderer.CreateNativeTargets(
                ReflectionSize >> mip, ReflectionSize >> mip, Format.R16G16B16A16_Float))).ToArray();
            var texture = renderer.Device.CreateTexture2D(Format.R16G16B16A16_Float, ReflectionSize, ReflectionSize,
                mipLevels: ReflectionMips, bindFlags: BindFlags.ShaderResource);
            try { reflection = Own(new GpuTexture(texture, renderer.Device.CreateShaderResourceView(texture),
                ReflectionSize, ReflectionSize, mipLevels: ReflectionMips)); }
            catch { texture.Dispose(); throw; }
        }
        catch { Dispose(); throw; }
    }

    private T Own<T>(T item) where T : IDisposable { owned.Add(item); return item; }

    /** The pass rejects changed TFX rather than filling unknown inputs. */
    private Pass CreatePass(GameMaterialReader reader, uint tag, string vertexProgram, string pixelProgram)
    {
        var execution = reader.ReadExecution(tag);
        if (Convert.ToHexString(execution.Vertex.TfxBytecode) != vertexProgram
            || Convert.ToHexString(execution.Pixel.TfxBytecode) != pixelProgram)
            throw new NotSupportedException($"Sky lighting technique {tag:X8} has a changed TFX program.");
        if (tag == GenerateTag && execution.Pixel.Shader!.Declarations
            .Where(line => line.StartsWith("dcl_input_ps", StringComparison.Ordinal))
            .Any(line => !line.Contains("v2.xy, position", StringComparison.Ordinal)))
            throw new NotSupportedException("The hemisphere shader now consumes camera-dependent interpolants.");
        var rasterizer = new RasterizerDescription(CullMode.None, FillMode.Solid) { DepthClipEnable = false };
        var depth = new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always);
        var blend = new BlendDescription(Blend.One, Blend.Zero);
        if (tag == ApplyTag)
        {
            var resolved = states.Resolve(0, execution.Material.StateOverrides, 0x81818181, NativeDepthTable.Alternate);
            rasterizer = resolved.Rasterizer; depth = resolved.DepthStencil; blend = resolved.Blend;
        }
        var pipeline = Own(renderer.CreateNativePipeline(reader, execution.Material, [], rasterizer, depth, blend));
        NativeConstantBuffer? Buffer(int rows) => rows == 0 ? null : Own(renderer.CreateNativeConstantBuffer(new byte[rows * 16]));
        return new(execution, pipeline, Buffer(execution.Vertex.ConstantTemplate.Length), Buffer(execution.Pixel.ConstantTemplate.Length));
    }

    /** The native full-hemisphere shader has a fixed 64 by 64 pixel mapping. */
    public bool Generate(NativeAtmosphereFrame frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!frame.Settings.HasMode0Lookups || !frame.Settings.TryPickKeyframe(frame.TimeOfDay, out float keyframe)) return false;
        if (ReferenceEquals(lastLookup, frame.FirstLookup) && ReferenceEquals(lastSecondLookup, frame.SecondLookup)
            && lastKeyframe == keyframe && lastLight == frame.Light) return true;
        // The fixed 64-pixel PS reads SV_Position; the VS direction output is unused.
        var inputs = new TfxInputs { Parameters = parameters };
        inputs.Matrices[new(2, 14)] = NativeAtmosphere.InvertPrecise(frame.Projection);
        NativeAtmosphere.ApplyParameters(inputs, slots, NativeAtmosphere.CameraHeight(frame.View));
        inputs.Floats[new(7, 12)] = NativeAtmosphereSettings.SliceCoordinate(keyframe);
        inputs.Vectors[new(7, 10)] = new(frame.Light, 0);
        inputs.Resources[new(7, 4)] = first;
        inputs.Resources[new(7, 5)] = second;
        UpdateView(frame.Camera, atmosphere.Width, atmosphere.Height);
        Run(generate, inputs, atmosphere, new() { [first] = frame.FirstLookup.View, [second] = frame.SecondLookup.View });
        var copy = new TfxInputs { Parameters = parameters };
        copy.Resources[new(37, 0)] = source;
        UpdateView(frame.Camera, tinted.Width, tinted.Height);
        Run(tint, copy, tinted, new() { [source] = atmosphere.Textures[0].View });
        renderer.Context.CopySubresourceRegion(reflection.Resource, 0, 0, 0, 0, tinted.Textures[0].Resource, 0);
        for (int mip = 1; mip < ReflectionMips; mip++)
        {
            var target = filtered[mip - 1];
            var filterInputs = new TfxInputs { Parameters = parameters };
            filterInputs.Resources[new(37, 0)] = source;
            filterInputs.Vectors[new(37, 11)] = new(mip - 1, 0.5f / target.Width, 0.5f / target.Height, 0);
            UpdateView(frame.Camera, target.Width, target.Height);
            Run(filter, filterInputs, target, new() { [source] = reflection.View });
            renderer.Context.CopySubresourceRegion(reflection.Resource, (uint)mip, 0, 0, 0, target.Textures[0].Resource, 0);
        }
        lastLookup = frame.FirstLookup; lastSecondLookup = frame.SecondLookup; lastKeyframe = keyframe; lastLight = frame.Light;
        GenerationCount++;
        return true;
    }

    /** The sky pass supplies diffuse and reflected-light buffers before deferred shading. */
    public void Render(NativeGBufferInputs gbuffer, NativeCameraScopeConstants camera, Matrix4x4 view,
        Matrix4x4 projection, double seconds, float ev, GpuTexture? reflectionSource = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (lastLookup is null && reflectionSource is null) throw new InvalidOperationException("Sky reflection has not been generated.");
        int width = gbuffer.Third.Width, height = gbuffer.Third.Height;
        if (lighting is null || lighting.Width != width || lighting.Height != height)
        {
            lighting?.Dispose();
            lighting = renderer.CreateNativeTargets(width, height,
                Format.R11G11B10_Float, Format.R11G11B10_Float, Format.R8_UNorm);
        }
        var inputs = NativeViewInputs.CreateCameraInputs(checked((ushort)width), checked((ushort)height), view, projection);
        inputs.Parameters = parameters;
        inputs.Resources[new(3, 11)] = third;
        inputs.Resources[new(3, 10)] = normal;
        inputs.Resources[new(3, 19)] = sky;
        var constants = NativeFrameScope.Create(new() { SimulationSeconds = seconds,
            FrameFlag425B8 = false, FrameFlag425B9 = false, Extern1C = MathF.Pow(2, ev) });
        renderer.UpdateNativeConstantBuffer(frameBuffer, MemoryMarshal.AsBytes(constants.Vectors.AsSpan()));
        UpdateView(camera, width, height);
        Run(apply, inputs, lighting, new() { [third] = gbuffer.Third.View, [normal] = gbuffer.Normal.View,
            [sky] = (reflectionSource ?? reflection).View });
    }

    private void UpdateView(NativeCameraScopeConstants camera, int width, int height)
    {
        var vertex = new Vector4[10];
        Array.Copy(camera.Vertex, vertex, 9);
        vertex[8] = NativeAtmosphere.SizeOf(width, height);
        vertex[9] = Vector4.UnitX;
        var pixel = (Vector4[])camera.Pixel.Clone();
        pixel[12] = NativeAtmosphere.SizeOf(width, height);
        renderer.UpdateNativeConstantBuffer(vertexView, MemoryMarshal.AsBytes(vertex.AsSpan()));
        renderer.UpdateNativeConstantBuffer(pixelView, MemoryMarshal.AsBytes(pixel.AsSpan()));
    }

    private void Run(Pass pass, TfxInputs inputs, NativeRenderTargets target,
        Dictionary<TfxResource, ID3D11ShaderResourceView> resources)
    {
        var vertex = TfxEvaluator.Evaluate(pass.Execution.Vertex, inputs);
        var pixel = TfxEvaluator.Evaluate(pass.Execution.Pixel, inputs);
        vertex.RequireComplete(); pixel.RequireComplete();
        if (pass.Vertex is not null) renderer.UpdateNativeConstantBuffer(pass.Vertex, MemoryMarshal.AsBytes(vertex.Constants.AsSpan()));
        if (pass.Pixel is not null) renderer.UpdateNativeConstantBuffer(pass.Pixel, MemoryMarshal.AsBytes(pixel.Constants.AsSpan()));
        pass.Bindings.MaterialEvaluations[GameShaderStage.Vertex] = vertex;
        pass.Bindings.MaterialEvaluations[GameShaderStage.Pixel] = pixel;
        foreach (var sampler in pixel.Samplers)
            if (!pass.Bindings.Samplers.ContainsKey(sampler.Key))
                pass.Bindings.Samplers[sampler.Key] = Own(renderer.Device.CreateSamplerState(sampler.Value.Description));
        pass.Bindings.ApplyTfxResources(pixel, resources);
        try { renderer.DrawNativeVertices(pass.Pipeline, pass.Bindings, target, PrimitiveTopology.TriangleStrip, 4); }
        finally
        {
            for (uint slot = 0; slot < 8; slot++) renderer.Context.PSSetShaderResource(slot, null!);
            renderer.RestoreMainTarget();
        }
    }

    /** Releases the GPU resources this pass owns. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lighting?.Dispose();
        for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        owned.Clear();
    }
}
