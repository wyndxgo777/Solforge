using System.Numerics;
using System.Runtime.InteropServices;
using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using SunriseStudio.Scene;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Viewport;

/** Everything the preview needs to draw one native mesh: material, mesh, instance, textures and render state. */
public sealed record NativePreviewSource(GameMaterialReader Reader, MaterialExecutionData Execution,
    NativeMeshData Mesh, NativeStaticBinding Instance, IReadOnlyDictionary<uint, TextureData> Textures,
    GameScopeExecution ViewScope, NativeRenderStateDescriptions States, TfxParameterLookup Parameters)
{
    /** Native preview needs the authored placement context and input declaration. */
    public static NativePreviewSource Read(PackageCatalog catalog, MeshInstance instance, bool assetPreview = false)
    {
        var mesh = instance.Mesh.Native ?? throw new NotSupportedException("This mesh has no native streams.");
        if (mesh.Kind != NativeMeshKind.Static || !mesh.InputLayoutVerified || !mesh.InputLayoutFitsSignature)
            throw new NotSupportedException("Native preview currently needs a verified static mesh declaration.");
        var binding = assetPreview && instance.InstanceSourceTag == 0 && instance.NativeInstanceData.IsEmpty
            ? NativeAssetBindings.CreateStatic(instance) : new AssetSceneLoader(catalog).ReadNativeStaticBinding(instance);
        var reader = new GameMaterialReader(catalog);
        var parameters = reader.ReadTfxParameterDefaults(0x80B9E57C).CreateSnapshot();
        var prepared = NativeScenePlan.ValidateMaterial(reader, new NativeRenderStateCatalog(), mesh.MaterialTag, parameters);
        var execution = prepared.Execution;
        var textures = execution.Material.Textures.Where(t => t.DeclaredByShader)
            .Select(t => t.TextureTag).Distinct().ToDictionary(t => t, t => new TextureDataReader(catalog).Read(t));
        return new(reader, execution, mesh, binding, textures, reader.ReadScopeExecution(0x80B356A5), prepared.States, parameters);
    }
}

/** This view exposes authored shader outputs before lighting and display conversion. */
public sealed class NativeMeshPreview : IDisposable
{
    private readonly Dx11Renderer renderer;
    private readonly List<IDisposable> owned = [];
    private readonly NativeMaterialPipeline pipeline;
    private readonly NativeGpuMesh mesh;
    private readonly NativeConstantBuffer viewConstants;
    private readonly NativeConstantBuffer pixelViewConstants;
    private readonly NativeConstantBuffer frameConstants;
    private readonly GameScopeExecution viewScope;
    private readonly TfxParameterLookup parameters;
    private readonly NativeOutputDisplay display;
    private readonly NativeDrawBindings bindings = new();
    private readonly MaterialStageData[] materialStages;
    public NativeRenderTargets Targets { get; }
    public uint MaterialTag => pipeline.MaterialTag;
    public string PassPolicy => NativeScenePlan.PassPolicy;
    public NativeStaticBinding Instance { get; }

    /** Every GPU resource belongs to this preview, while package data remains caller-owned. */
    public NativeMeshPreview(Dx11Renderer renderer, NativePreviewSource source)
    {
        this.renderer = renderer;
        Instance = source.Instance;
        materialStages = [source.Execution.Vertex, source.Execution.Pixel];
        viewScope = source.ViewScope;
        parameters = source.Parameters;
        T Own<T>(T resource) where T : IDisposable { owned.Add(resource); return resource; }
        try
        {
            var elements = source.Mesh.InputElements.Select(e => new InputElementDescription(e.Semantic,
                e.SemanticIndex, (Format)e.Format, (uint)e.Offset, (uint)e.Slot,
                e.PerInstance ? InputClassification.PerInstanceData : InputClassification.PerVertexData,
                (uint)e.StepRate)).ToArray();
            pipeline = Own(renderer.CreateNativePipeline(source.Reader, source.Execution.Material, elements,
                source.States.Rasterizer, source.States.DepthStencil, source.States.Blend));
            if (source.Mesh.BaseVertex != 0) throw new NotSupportedException("Native preview has no base-vertex binding.");
            mesh = Own(renderer.CreateNativeMesh(source.Mesh.VertexStreams.Select(v =>
                    new NativeVertexStreamUpload(v.Data, (uint)v.Stride)).ToArray(), source.Mesh.IndexData,
                source.Mesh.Is32Bit ? Format.R32_UInt : Format.R16_UInt,
                source.Mesh.FirstIndex, source.Mesh.IndexCount,
                source.Mesh.GamePrimitiveType == 3 ? PrimitiveTopology.TriangleList : PrimitiveTopology.TriangleStrip));
            Targets = Own(renderer.CreateNativeTargets(768, 768,
                Enumerable.Repeat(Format.R16G16B16A16_Float, (int)pipeline.RequiredTargetCount).ToArray()));
            foreach (var stage in materialStages)
            {
                var evaluated = TfxEvaluator.Evaluate(stage, NativeScenePlan.MaterialInputs(0, parameters));
                evaluated.RequireComplete();
                bindings.MaterialEvaluations[stage.Stage] = evaluated;
                if (evaluated.Constants.Length != 0)
                    bindings.ConstantBuffers[new(stage.Stage, 0)] = Own(renderer.CreateNativeConstantBuffer(
                        MemoryMarshal.AsBytes(evaluated.Constants.AsSpan())));
                foreach (var sampler in evaluated.Samplers)
                    bindings.Samplers[sampler.Key] = Own(renderer.Device.CreateSamplerState(sampler.Value.Description));
            }
            var textures = new Dictionary<uint, GpuTexture>();
            foreach (var item in source.Textures)
            {
                var t = item.Value;
                textures.Add(item.Key, Own(renderer.CreateTextureResource(t.Width, t.Height, t.Depth,
                    t.ArraySize, t.MipCount, (Format)t.Format, t.IsCube, t.IsVolume,
                    t.Subresources.Select(s => new TextureSubresourceUpload(s.MipLevel, s.ArraySlice,
                        s.Data, s.RowPitch, s.SlicePitch)).ToArray())));
            }
            foreach (var binding in source.Execution.Material.Textures.Where(t => t.DeclaredByShader))
                bindings.Resources[new(binding.Stage, binding.Slot)] = textures[binding.TextureTag].View;
            var scalar = Own(renderer.CreateNativeBufferResource(source.Instance.ScalarBuffer.Data,
                (Format)source.Instance.ScalarFormat));
            bindings.Resources[new(GameShaderStage.Vertex, 0)] = scalar.View;
            var instances = new Vector4[64];
            source.Instance.InstanceConstants.CopyTo(instances, 0);
            bindings.ConstantBuffers[new(GameShaderStage.Vertex, 11)] = Own(renderer.CreateNativeConstantBuffer(
                MemoryMarshal.AsBytes(instances.AsSpan()), new Dictionary<uint, uint> { [15] = source.Instance.PackedWordKnownMask }));
            viewConstants = Own(renderer.CreateNativeConstantBuffer(new byte[14 * 16], NativeViewInputs.UnknownVertexWords));
            pixelViewConstants = Own(renderer.CreateNativeConstantBuffer(new byte[14 * 16], NativeViewInputs.UnknownPixelWords));
            var frame = NativeScenePlan.InspectionFrame(0);
            frameConstants = Own(renderer.CreateNativeConstantBuffer(MemoryMarshal.AsBytes(frame.Vectors.AsSpan()), frame.KnownWordMasks));
            foreach (var stage in materialStages)
                if (stage.Shader is { } shader && NativeShaderContracts.CanUseFrameConstants(
                    source.Reader.ReadShaderBytecode(shader), stage.Stage, frame))
                    bindings.ConstantBuffers[new(stage.Stage, 13)] = frameConstants;
            display = Own(new NativeOutputDisplay(renderer, 768, 768));
            bindings.ConstantBuffers[new(GameShaderStage.Vertex, 12)] = viewConstants;
            if (NativeShaderContracts.CanUseKnownPixelView(source.Execution.Material.PixelShader))
                bindings.ConstantBuffers[new(GameShaderStage.Pixel, 12)] = pixelViewConstants;
            var missing = pipeline.Validate(bindings, Targets);
            if (missing.Count != 0) throw new NotSupportedException(string.Join(" ", missing));
        }
        catch { Dispose(); throw; }
    }

    /** Material TFX receives only verified frame inputs; scope buffers keep separate stage layouts. */
    public void Render(ViewerCamera camera, double simulationSeconds)
    {
        var projection = camera.Projection((float)Targets.Width / Targets.Height);
        var inputs = NativeScenePlan.MaterialInputs(simulationSeconds, parameters, NativeViewInputs.CreateCameraInputs(
            checked((ushort)Targets.Width), checked((ushort)Targets.Height), camera.View, projection));
        var frame = NativeScenePlan.InspectionFrame(simulationSeconds);
        renderer.UpdateNativeConstantBuffer(frameConstants, MemoryMarshal.AsBytes(frame.Vectors.AsSpan()));
        foreach (var stage in materialStages.Where(s => s.TfxBytecode.Length != 0))
        {
            var evaluation = TfxEvaluator.Evaluate(stage, inputs);
            evaluation.RequireComplete();
            bindings.MaterialEvaluations[stage.Stage] = evaluation;
            if (bindings.ConstantBuffers.TryGetValue(new(stage.Stage, 0), out var constants))
                renderer.UpdateNativeConstantBuffer(constants, MemoryMarshal.AsBytes(evaluation.Constants.AsSpan()));
        }
        var view = NativeViewInputs.EvaluateCameraScope(viewScope, checked((ushort)Targets.Width),
            checked((ushort)Targets.Height), camera.View, projection);
        renderer.UpdateNativeConstantBuffer(viewConstants, MemoryMarshal.AsBytes(view.Vertex.AsSpan()));
        renderer.UpdateNativeConstantBuffer(pixelViewConstants, MemoryMarshal.AsBytes(view.PixelRows.AsSpan()));
        try
        {
            renderer.ClearNativeTargets(Targets, new Vector4(0.012f, 0.016f, 0.022f, 1), 1);
            renderer.DrawNative(pipeline, mesh, bindings, Targets, 1);
        }
        finally { renderer.RestoreMainTarget(); }
    }

    /** Renders one output channel to a displayable texture. */
    public GpuTexture Display(int output, int channel, bool srgb, float exposure) =>
        display.Render(Targets.Textures[output], channel, srgb, exposure);

    /** Disposes owned GPU resources in reverse creation order. */
    public void Dispose()
    {
        for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        owned.Clear();
    }
}
