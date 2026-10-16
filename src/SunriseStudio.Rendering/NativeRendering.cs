using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D11.Shader;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SunriseStudio.Rendering;

/**
 * Identifies a shader binding slot by stage and register. This file holds the native-pipeline GPU
 * resource types.
 */
public readonly record struct NativeBindingKey(GameShaderStage Stage, uint Slot);
/** Vertex stream bytes with their stride, ready for upload. */
public sealed record NativeVertexStreamUpload(byte[] Data, uint Stride);
/** Typeless resource format plus the render-target and shader-view formats used with it. */
public readonly record struct NativeColorTargetFormat(Format Resource, Format RenderTarget, Format ShaderResource);
/** A constant buffer slot a shader reads and how many 16-byte vectors it needs. */
internal readonly record struct NativeConstantRequirement(NativeBindingKey Key, uint Vectors);
/** A shader resource slot and the view dimension the shader expects. */
internal readonly record struct NativeResourceRequirement(NativeBindingKey Key, string Dimension);

/** Every shader dependency is supplied explicitly; missing resources have no zero fallback. */
public sealed class NativeDrawBindings
{
    public Dictionary<NativeBindingKey, NativeConstantBuffer> ConstantBuffers { get; } = [];
    public Dictionary<NativeBindingKey, ID3D11ShaderResourceView> Resources { get; } = [];
    public Dictionary<NativeBindingKey, ID3D11SamplerState> Samplers { get; } = [];
    public Dictionary<GameShaderStage, TfxEvaluation> MaterialEvaluations { get; } = [];
    public uint? StencilReference { get; set; }
    internal Dictionary<NativeBindingKey, (TfxResource Resource, ID3D11ShaderResourceView View)> AppliedTfxResources { get; } = [];

    /** Resolve every typed resource before changing any binding; views remain caller-owned. */
    public void ApplyTfxResources(TfxEvaluation evaluation,
        IReadOnlyDictionary<TfxResource, ID3D11ShaderResourceView> resourceViews)
    {
        evaluation.RequireComplete();
        if (evaluation.ResourceBindings.Count == 0) return;
        if (evaluation.ResourceBindings is not Dictionary<NativeBindingKey, TfxResource> bindings)
            bindings = new(evaluation.ResourceBindings);
        foreach (var item in bindings)
        {
            if (!resourceViews.TryGetValue(item.Value, out var view) || view is null || view.NativePointer == IntPtr.Zero)
                throw new InvalidOperationException($"TFX resource {item.Value.Name} has no live shader view.");
            // A view applied before was checked against the same dimension then.
            bool known = AppliedTfxResources.TryGetValue(item.Key, out var applied)
                && ReferenceEquals(applied.View, view) && applied.Resource.Dimension == item.Value.Dimension;
            if (!known && view.Description.ViewDimension != item.Value.Dimension)
                throw new InvalidOperationException($"TFX resource {item.Value.Name} needs {item.Value.Dimension}.");
        }
        foreach (var item in bindings)
        {
            var view = resourceViews[item.Value];
            Resources[item.Key] = view;
            AppliedTfxResources[item.Key] = (item.Value, view);
        }
    }
}

/** Buffer size stays fixed so shader declaration checks also cover later updates. */
public sealed class NativeConstantBuffer : IDisposable
{
    internal ID3D11Buffer Buffer { get; }
    public uint Size { get; }
    public IReadOnlyDictionary<uint, uint> KnownWordMasks { get; }
    internal NativeConstantBuffer(ID3D11Buffer buffer, uint size,
        IReadOnlyDictionary<uint, uint>? knownWordMasks)
    {
        Buffer = buffer; Size = size;
        KnownWordMasks = new ReadOnlyDictionary<uint, uint>(knownWordMasks is null
            ? new Dictionary<uint, uint>() : new Dictionary<uint, uint>(knownWordMasks));
    }
    /** Releases the buffer. */
    public void Dispose() => Buffer.Dispose();
}

/** A typed buffer owns both storage and the shader view used by native resource loads. */
public sealed class NativeBufferResource : IDisposable
{
    internal ID3D11Buffer Buffer { get; }
    public ID3D11ShaderResourceView View { get; }
    internal NativeBufferResource(ID3D11Buffer buffer, ID3D11ShaderResourceView view)
    { Buffer = buffer; View = view; }
    /** Releases the view and the buffer. */
    public void Dispose() { View.Dispose(); Buffer.Dispose(); }
}

/** Shared buffers retain their original index memory until every draw range has been released. */
public sealed class NativeGpuGeometry : IDisposable
{
    internal ID3D11Buffer[] Streams { get; }
    internal uint[] Strides { get; }
    internal ID3D11Buffer Indices { get; }
    internal Format IndexFormat { get; }
    internal ReadOnlyMemory<byte> SourceIndices { get; }
    internal uint VertexCount { get; }
    internal bool IsDisposed { get; private set; }
    public uint IndexCount { get; }

    internal NativeGpuGeometry(ID3D11Buffer[] streams, uint[] strides, ID3D11Buffer indices,
        Format indexFormat, ReadOnlyMemory<byte> sourceIndices, uint vertexCount)
    {
        Streams = streams; Strides = strides; Indices = indices; IndexFormat = indexFormat;
        SourceIndices = sourceIndices; VertexCount = vertexCount;
        IndexCount = (uint)sourceIndices.Length / (indexFormat == Format.R16_UInt ? 2u : 4u);
    }

    /** Releases the vertex streams and index buffer. */
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Indices.Dispose();
        foreach (var stream in Streams) stream.Dispose();
    }
}

/** A shared geometry owner must outlive all draw ranges that reference it. */
public sealed class NativeGpuMesh : IDisposable
{
    internal NativeGpuGeometry Geometry { get; }
    internal ID3D11Buffer[] Streams => Geometry.Streams;
    internal uint[] Strides => Geometry.Strides;
    internal ID3D11Buffer Indices => Geometry.Indices;
    internal Format IndexFormat => Geometry.IndexFormat;
    internal bool IsDisposed { get; private set; }
    private readonly bool ownsGeometry;
    public uint FirstIndex { get; }
    public uint IndexCount { get; }
    public PrimitiveTopology Topology { get; }

    internal NativeGpuMesh(NativeGpuGeometry geometry, uint firstIndex, uint indexCount,
        PrimitiveTopology topology, bool ownsGeometry = false)
    {
        Geometry = geometry; this.ownsGeometry = ownsGeometry;
        FirstIndex = firstIndex; IndexCount = indexCount; Topology = topology;
    }

    /** Releases the geometry if this mesh owns it. */
    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        if (ownsGeometry) Geometry.Dispose();
    }
}

/** These surfaces expose raw shader outputs; their formats are chosen by the caller. */
public sealed class NativeRenderTargets : IDisposable
{
    internal ID3D11RenderTargetView[] Views { get; }
    internal ID3D11Texture2D Depth { get; }
    internal ID3D11DepthStencilView DepthView { get; }
    public ID3D11ShaderResourceView? DepthShaderView { get; }
    public IReadOnlyList<GpuTexture> Textures { get; }
    public IReadOnlyList<NativeColorTargetFormat> ColorFormats { get; }
    public int Width { get; }
    public int Height { get; }
    public Format DepthFormat { get; }
    public bool HasStencil => DepthFormat is Format.D24_UNorm_S8_UInt or Format.D32_Float_S8X24_UInt;
    /** False when the depth texture belongs to another target set; only the view is released. */
    public bool OwnsDepth { get; }

    internal NativeRenderTargets(GpuTexture[] textures, ID3D11RenderTargetView[] views,
        ID3D11Texture2D depth, ID3D11DepthStencilView depthView, int width, int height, Format depthFormat,
        ID3D11ShaderResourceView? depthShaderView, IReadOnlyList<NativeColorTargetFormat> colorFormats,
        bool ownsDepth = true)
    {
        Textures = textures; Views = views; Depth = depth; DepthView = depthView;
        Width = width; Height = height;
        DepthFormat = depthFormat;
        DepthShaderView = depthShaderView;
        ColorFormats = colorFormats;
        OwnsDepth = ownsDepth;
    }

    /** Releases the colour textures and views, and the depth resources when owned. */
    public void Dispose()
    {
        DepthShaderView?.Dispose(); DepthView.Dispose();
        if (OwnsDepth) Depth.Dispose();
        foreach (var view in Views) view.Dispose();
        foreach (var texture in Textures) texture.Dispose();
    }
}

/** Original shader bytecode is paired with caller-supplied input and fixed-function state. */
public sealed class NativeMaterialPipeline : IDisposable
{
    internal ID3D11VertexShader VertexShader { get; }
    /** Null for a depth-only material, such as a shadow caster. */
    internal ID3D11PixelShader? PixelShader { get; }
    internal ID3D11InputLayout? Layout { get; }
    internal ID3D11RasterizerState Rasterizer { get; }
    internal ID3D11DepthStencilState Depth { get; }
    internal ID3D11BlendState Blend { get; }
    internal IReadOnlyList<NativeConstantRequirement> Constants { get; }
    internal IReadOnlyList<NativeResourceRequirement> Resources { get; }
    internal IReadOnlyList<NativeBindingKey> Samplers { get; }
    internal InputElementDescription[] Elements { get; }
    internal GameShaderStage[] RequiredTfxStages { get; }
    internal string VertexDigest { get; }
    internal string PixelDigest { get; }
    internal bool RequiresStencilReference { get; }
    public uint MaterialTag { get; }
    public uint RequiredTargetCount { get; }

    internal NativeMaterialPipeline(ID3D11Device device, GameMaterialReader reader,
        GameMaterialInfo material, InputElementDescription[] elements,
        RasterizerDescription rasterizer, DepthStencilDescription depth, BlendDescription blend, uint boundTargets = 0)
    {
        if (material.AdditionalShaderHeaders.Count != 0)
            throw new NotSupportedException("Native draw has unsupported intermediate shader stages: "
                + string.Join(", ", material.AdditionalShaderHeaders.Select(s => $"0x{s.Tag:X8} at +0x{s.FieldOffset:X}")));
        if (material.ComputeShader is not null)
            throw new NotSupportedException("Native graphics draw cannot execute the material's compute stage.");
        var vs = material.VertexShader ?? throw new InvalidDataException("Native material has no vertex shader.");
        // A material with no pixel shader draws depth only.
        var ps = material.PixelShader;
        var execution = reader.ReadExecution(material.Tag);
        RequiresStencilReference = depth.StencilEnable;
        RequiredTfxStages = new[] { execution.Vertex, execution.Pixel }
            .Where(s => s.TfxBytecode.Length != 0).Select(s => s.Stage).ToArray();
        var constants = new List<NativeConstantRequirement>();
        var resources = new List<NativeResourceRequirement>();
        var samplers = new List<NativeBindingKey>();
        foreach (var shader in ps is null ? new[] { vs } : new[] { vs, ps })
        {
            foreach (string declaration in shader.Declarations)
            {
                var cb = Regex.Match(declaration, @"^dcl_constantbuffer CB(\d+)\[(\d+)\]");
                var sampler = Regex.Match(declaration, @"^dcl_sampler s(\d+),");
                if (cb.Success)
                    constants.Add(new(new(shader.Stage, uint.Parse(cb.Groups[1].Value)),
                        uint.Parse(cb.Groups[2].Value)));
                if (sampler.Success) samplers.Add(new(shader.Stage, uint.Parse(sampler.Groups[1].Value)));
                if (declaration.StartsWith("dcl_uav", StringComparison.Ordinal)
                    || declaration.StartsWith("dcl_interface", StringComparison.Ordinal))
                    throw new NotSupportedException($"Native draw does not support {declaration}.");
            }
            resources.AddRange(shader.TextureSlots.Select(x =>
                new NativeResourceRequirement(new(shader.Stage, x.Slot), x.Dimension)));
        }
        uint targets = 0;
        foreach (string declaration in ps?.Declarations ?? [])
        {
            var output = Regex.Match(declaration, @"^dcl_output o(\d+)\.");
            if (output.Success) targets = Math.Max(targets, uint.Parse(output.Groups[1].Value) + 1);
        }
        if (targets > 8) throw new NotSupportedException("Native pixel outputs are not supported.");
        // Instance data steps once per instance in slots after every mesh stream; the draw list binds them.
        int lastVertexSlot = elements.Where(e => e.Classification == InputClassification.PerVertexData)
            .Select(e => (int)e.Slot).DefaultIfEmpty(-1).Max();
        if (elements.Any(e => e.Classification != InputClassification.PerVertexData
            && (e.Classification != InputClassification.PerInstanceData || e.InstanceDataStepRate != 1 || (int)e.Slot <= lastVertexSlot)))
            throw new NotSupportedException("Native mesh streams must contain per-vertex input data.");
        byte[] vertexCode = reader.ReadShaderBytecode(vs);
        byte[]? pixelCode = ps is null ? null : reader.ReadShaderBytecode(ps);
        VertexDigest = Convert.ToHexString(SHA256.HashData(vertexCode));
        PixelDigest = pixelCode is null ? "" : Convert.ToHexString(SHA256.HashData(pixelCode));
        NativeShaderUsage.Get(vertexCode); if (pixelCode is not null) NativeShaderUsage.Get(pixelCode);
        using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(vertexCode);
        var inputs = reflection.InputParameters.Where(p => p.SystemValueType == 0).ToArray();
        Elements = elements.Where(e => inputs.Any(p => p.SemanticIndex == e.SemanticIndex
            && string.Equals(p.SemanticName, e.SemanticName, StringComparison.OrdinalIgnoreCase))).ToArray();
        try
        {
            VertexShader = device.CreateVertexShader(vertexCode);
            if (pixelCode is not null) PixelShader = device.CreatePixelShader(pixelCode);
            if (inputs.Length != 0) Layout = device.CreateInputLayout(elements, vertexCode);
            Rasterizer = device.CreateRasterizerState(rasterizer);
            Depth = device.CreateDepthStencilState(depth);
            Blend = device.CreateBlendState(blend);
        }
        catch
        {
            Blend?.Dispose(); Depth?.Dispose(); Rasterizer?.Dispose(); Layout?.Dispose();
            PixelShader?.Dispose(); VertexShader?.Dispose();
            throw;
        }
        Constants = constants; Resources = resources; Samplers = samplers;
        MaterialTag = material.Tag;
        RequiredTargetCount = boundTargets == Dx11Renderer.NoColorTargets ? 0 : boundTargets == 0 ? targets : Math.Min(targets, boundTargets);
    }

    /** All missing bindings are reported before the immediate context is changed. */
    public IReadOnlyList<string> Validate(NativeDrawBindings bindings, NativeRenderTargets targets)
    {
        var missing = new List<string>();
        if (RequiresStencilReference)
        {
            if (!targets.HasStencil) missing.Add("Native depth/stencil state needs a stencil-capable target.");
            if (bindings.StencilReference is null) missing.Add("Native stencil reference is missing.");
        }
        if (bindings.StencilReference > byte.MaxValue) missing.Add("Native stencil reference exceeds eight bits.");
        foreach (var key in bindings.Resources.Keys)
            if (key.Stage is not (GameShaderStage.Vertex or GameShaderStage.Pixel))
                missing.Add($"Native graphics drawing cannot apply {key.Stage} resource t{key.Slot}.");
        foreach (var stage in RequiredTfxStages)
        {
            if (!bindings.MaterialEvaluations.TryGetValue(stage, out var evaluation))
                missing.Add($"{stage} material TFX has not been evaluated.");
            else if (!evaluation.IsComplete)
                missing.AddRange(evaluation.MissingRequirements.Select(x => $"{stage}: {x}"));
            else
                foreach (var item in evaluation.ResourceBindings)
                    if (!bindings.AppliedTfxResources.TryGetValue(item.Key, out var applied)
                        || !ReferenceEquals(applied.Resource, item.Value)
                        || applied.View.NativePointer == IntPtr.Zero
                        || !bindings.Resources.TryGetValue(item.Key, out var actual)
                        || actual.NativePointer != applied.View.NativePointer)
                        missing.Add($"{item.Key.Stage} TFX resource t{item.Key.Slot} ({item.Value.Name}) has not been applied.");
        }
        if (targets.Textures.Count < RequiredTargetCount)
            missing.Add($"Pixel shader needs {RequiredTargetCount} render targets.");
        foreach (var need in Constants)
        {
            if (!bindings.ConstantBuffers.TryGetValue(need.Key, out var buffer)
                || buffer.Size < need.Vectors * 16)
            {
                missing.Add($"{need.Key.Stage} CB{need.Key.Slot} needs {need.Vectors} vectors.");
                continue;
            }
            string digest = need.Key.Stage == GameShaderStage.Vertex ? VertexDigest : PixelDigest;
            foreach (var word in buffer.KnownWordMasks)
            {
                uint used = NativeShaderContracts.RequiredWordMask(digest, need.Key, word.Key);
                if ((word.Value & used) != used)
                    missing.Add($"{need.Key.Stage} CB{need.Key.Slot} word {word.Key} needs unknown bits 0x{used & ~word.Value:X8}.");
            }
        }
        foreach (var need in Samplers)
            if (!bindings.Samplers.ContainsKey(need)) missing.Add($"{need.Stage} sampler s{need.Slot} is missing.");
        foreach (var need in Resources)
        {
            if (!bindings.Resources.TryGetValue(need.Key, out var view))
            { missing.Add($"{need.Key.Stage} resource t{need.Key.Slot} ({need.Dimension}) is missing."); continue; }
            var expected = need.Dimension switch
            {
                "buffer" => ShaderResourceViewDimension.Buffer,
                "texture1d" => ShaderResourceViewDimension.Texture1D,
                "texture1darray" => ShaderResourceViewDimension.Texture1DArray,
                "texture2d" => ShaderResourceViewDimension.Texture2D,
                "texture2darray" => ShaderResourceViewDimension.Texture2DArray,
                "texture3d" => ShaderResourceViewDimension.Texture3D,
                "texturecube" => ShaderResourceViewDimension.TextureCube,
                "texturecubearray" => ShaderResourceViewDimension.TextureCubeArray,
                _ => ShaderResourceViewDimension.Unknown
            };
            if (expected == ShaderResourceViewDimension.Unknown || view.Description.ViewDimension != expected)
                missing.Add($"{need.Key.Stage} resource t{need.Key.Slot} needs {need.Dimension}.");
        }
        return missing;
    }

    /** Releases the shaders and pipeline state. */
    public void Dispose()
    {
        Blend.Dispose(); Depth.Dispose(); Rasterizer.Dispose(); Layout?.Dispose();
        PixelShader?.Dispose(); VertexShader.Dispose();
    }
}

/** Native-pipeline part of Dx11Renderer: geometry, constant buffers, pipelines, draws and pixel readback. */
public sealed unsafe partial class Dx11Renderer
{
    /** The standalone overload owns its geometry; shared ranges use CreateNativeGeometry instead. */
    public NativeGpuMesh CreateNativeMesh(IReadOnlyList<NativeVertexStreamUpload> streams,
        ReadOnlySpan<byte> indexData, Format indexFormat, uint firstIndex, uint indexCount,
        PrimitiveTopology topology)
    {
        var geometry = CreateNativeGeometry(streams, indexData.ToArray(), indexFormat);
        try
        {
            ValidateNativeRange(geometry, firstIndex, indexCount, topology);
            return new(geometry, firstIndex, indexCount, topology, true);
        }
        catch { geometry.Dispose(); throw; }
    }

    /** Original index memory must remain unchanged for later draw-range validation. */
    public NativeGpuGeometry CreateNativeGeometry(IReadOnlyList<NativeVertexStreamUpload> streams,
        ReadOnlyMemory<byte> indexData, Format indexFormat)
    {
        if (streams.Count is < 1 or > 32)
            throw new ArgumentException("Native geometry stream count is unsupported.");
        uint indexSize = indexFormat switch
        { Format.R16_UInt => 2, Format.R32_UInt => 4, _ => throw new ArgumentException("Native index format is invalid.") };
        if (indexData.IsEmpty || indexData.Length % indexSize != 0)
            throw new ArgumentException("Native index data does not contain complete indices.");
        uint vertexCount = uint.MaxValue;
        foreach (var stream in streams)
        {
            if (stream.Stride == 0 || stream.Data.Length == 0 || stream.Data.Length % stream.Stride != 0)
                throw new ArgumentException("Native vertex stream has an invalid stride.");
            vertexCount = Math.Min(vertexCount, (uint)stream.Data.Length / stream.Stride);
        }
        var buffers = new List<ID3D11Buffer>();
        try
        {
            foreach (var stream in streams)
            {
                buffers.Add(Device.CreateBuffer<byte>(stream.Data, BindFlags.VertexBuffer));
            }
            var indices = Device.CreateBuffer(indexData.Span, BindFlags.IndexBuffer);
            return new(buffers.ToArray(), streams.Select(s => s.Stride).ToArray(), indices,
                indexFormat, indexData, vertexCount);
        }
        catch { foreach (var buffer in buffers) buffer.Dispose(); throw; }
    }

    /** Creates a draw range over shared geometry. */
    public NativeGpuMesh CreateNativeMesh(NativeGpuGeometry geometry, uint firstIndex,
        uint indexCount, PrimitiveTopology topology)
    {
        ValidateNativeRange(geometry, firstIndex, indexCount, topology);
        return new(geometry, firstIndex, indexCount, topology);
    }

    /** One index buffer can serve different vertex groups, so bounds are checked per draw range. */
    private static void ValidateNativeRange(NativeGpuGeometry geometry, uint firstIndex,
        uint indexCount, PrimitiveTopology topology)
    {
        ObjectDisposedException.ThrowIf(geometry.IsDisposed, geometry);
        if (indexCount == 0 || topology is not (PrimitiveTopology.TriangleList or PrimitiveTopology.TriangleStrip)
            || (ulong)firstIndex + indexCount > geometry.IndexCount)
            throw new ArgumentException("Native draw range or topology is invalid.");
        uint indexSize = geometry.IndexFormat == Format.R16_UInt ? 2u : 4u;
        uint restart = indexSize == 2 ? ushort.MaxValue : uint.MaxValue;
        var data = geometry.SourceIndices.Span;
        for (uint i = 0; i < indexCount; i++)
        {
            var bytes = data.Slice(checked((int)((firstIndex + i) * indexSize)), (int)indexSize);
            uint index = indexSize == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (topology == PrimitiveTopology.TriangleStrip && index == restart) continue;
            if (index >= geometry.VertexCount) throw new ArgumentException("Native index references an absent vertex.");
        }
    }

    /** boundTargets value of a depth-only pass, such as shadow_generate: every pixel output is discarded. */
    public const uint NoColorTargets = uint.MaxValue;

    /**
     * Creates the VS/PS pipeline of one material.
     * @param boundTargets Colour targets the pass binds; outputs past them are discarded. Zero binds one per output.
     */
    public NativeMaterialPipeline CreateNativePipeline(GameMaterialReader reader, GameMaterialInfo material,
        InputElementDescription[] elements, RasterizerDescription rasterizer,
        DepthStencilDescription depth, BlendDescription blend, uint boundTargets = 0) =>
        new(Device, reader, material, elements, rasterizer, depth, blend, boundTargets);

    /** Copies a whole texture; both must match in size, format and mip count. */
    public void CopyTexture(GpuTexture destination, GpuTexture source) => Context.CopyResource(destination.Resource, source.Resource);

    /** Targets retain each pixel-shader output without assuming the game's lighting resolve. */
    public NativeRenderTargets CreateNativeTargets(int width, int height, params Format[] formats)
        => CreateNativeTargets(width, height, formats, Format.D32_Float);

    /** A stencil-enabled state needs a target with a stencil plane. */
    public NativeRenderTargets CreateNativeTargets(int width, int height, Format[] formats, Format depthFormat,
        bool shaderReadableDepth = false)
        => CreateNativeTargets(width, height, formats.Select(format =>
            new NativeColorTargetFormat(format, format, format)).ToArray(), depthFormat, shaderReadableDepth);

    /** Typeless storage keeps the authored render-target and shader-view formats distinct. */
    public NativeRenderTargets CreateNativeTargets(int width, int height, IReadOnlyList<NativeColorTargetFormat> formats,
        Format depthFormat, bool shaderReadableDepth = false)
    {
        // A depth-only set, such as a shadow map, has no colour target and a readable depth.
        if (width <= 0 || height <= 0 || formats.Count > 8 || formats.Count == 0 && !shaderReadableDepth)
            throw new ArgumentException("Native target dimensions or count are invalid.");
        if (depthFormat is not (Format.D16_UNorm or Format.D24_UNorm_S8_UInt
            or Format.D32_Float or Format.D32_Float_S8X24_UInt))
            throw new ArgumentException("Native depth target format is unsupported.", nameof(depthFormat));
        var textures = new List<GpuTexture>();
        var views = new List<ID3D11RenderTargetView>();
        ID3D11Texture2D? nativeDepth = null;
        ID3D11DepthStencilView? depthView = null;
        ID3D11ShaderResourceView? depthShaderView = null;
        try
        {
            foreach (var format in formats)
            {
                var texture = Device.CreateTexture2D(format.Resource, (uint)width, (uint)height, mipLevels: 1,
                    bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource);
                try
                {
                    textures.Add(new(texture, Device.CreateShaderResourceView(texture,
                    new ShaderResourceViewDescription(ShaderResourceViewDimension.Texture2D, format.ShaderResource, 0, 1)), width, height));
                }
                catch { texture.Dispose(); throw; }
                views.Add(Device.CreateRenderTargetView(texture,
                    new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, format.RenderTarget)));
            }
            var depthFormats = depthFormat switch
            {
                Format.D16_UNorm => (Format.R16_Typeless, Format.R16_UNorm),
                Format.D24_UNorm_S8_UInt => (Format.R24G8_Typeless, Format.R24_UNorm_X8_Typeless),
                Format.D32_Float => (Format.R32_Typeless, Format.R32_Float),
                Format.D32_Float_S8X24_UInt => (Format.R32G8X24_Typeless, Format.R32_Float_X8X24_Typeless),
                _ => throw new ArgumentException("Unsupported depth format.")
            };
            nativeDepth = Device.CreateTexture2D(shaderReadableDepth ? depthFormats.Item1 : depthFormat,
                (uint)width, (uint)height, mipLevels: 1,
                bindFlags: BindFlags.DepthStencil | (shaderReadableDepth ? BindFlags.ShaderResource : BindFlags.None));
            depthView = Device.CreateDepthStencilView(nativeDepth,
                new DepthStencilViewDescription(DepthStencilViewDimension.Texture2D, depthFormat));
            if (shaderReadableDepth)
                depthShaderView = Device.CreateShaderResourceView(nativeDepth,
                    new ShaderResourceViewDescription(ShaderResourceViewDimension.Texture2D, depthFormats.Item2, 0, 1));
            return new(textures.ToArray(), views.ToArray(), nativeDepth,
                depthView, width, height, depthFormat, depthShaderView, formats.ToArray());
        }
        catch
        {
            depthShaderView?.Dispose(); depthView?.Dispose(); nativeDepth?.Dispose();
            foreach (var view in views) view.Dispose();
            foreach (var texture in textures) texture.Dispose();
            throw;
        }
    }

    /** The new color targets test against the owner's depth; the owner must outlive them. */
    public NativeRenderTargets CreateNativeTargetsOnDepth(IReadOnlyList<NativeColorTargetFormat> formats,
        NativeRenderTargets depthOwner)
    {
        ArgumentNullException.ThrowIfNull(depthOwner);
        if (formats.Count is < 1 or > 8) throw new ArgumentException("Native target count is invalid.");
        var textures = new List<GpuTexture>();
        var views = new List<ID3D11RenderTargetView>();
        ID3D11DepthStencilView? depthView = null;
        try
        {
            foreach (var format in formats)
            {
                var texture = Device.CreateTexture2D(format.Resource, (uint)depthOwner.Width, (uint)depthOwner.Height,
                    mipLevels: 1, bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource);
                try
                {
                    textures.Add(new(texture, Device.CreateShaderResourceView(texture,
                        new ShaderResourceViewDescription(ShaderResourceViewDimension.Texture2D, format.ShaderResource, 0, 1)),
                        depthOwner.Width, depthOwner.Height));
                }
                catch { texture.Dispose(); throw; }
                views.Add(Device.CreateRenderTargetView(texture,
                    new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, format.RenderTarget)));
            }
            depthView = Device.CreateDepthStencilView(depthOwner.Depth,
                new DepthStencilViewDescription(DepthStencilViewDimension.Texture2D, depthOwner.DepthFormat));
            return new(textures.ToArray(), views.ToArray(), depthOwner.Depth, depthView, depthOwner.Width,
                depthOwner.Height, depthOwner.DepthFormat, null, formats.ToArray(), ownsDepth: false);
        }
        catch
        {
            depthView?.Dispose();
            foreach (var view in views) view.Dispose();
            foreach (var texture in textures) texture.Dispose();
            throw;
        }
    }

    /** Color attachments clear while the shared depth keeps its earlier pass result. */
    public void ClearNativeColorTargets(NativeRenderTargets targets, Vector4 clear)
    {
        foreach (var view in targets.Views)
            Context.ClearRenderTargetView(view, new Color4(clear.X, clear.Y, clear.Z, clear.W));
    }

    /** Direct3D constant buffers require complete 16-byte vectors and a 64 KiB limit. */
    public NativeConstantBuffer CreateNativeConstantBuffer(ReadOnlySpan<byte> bytes,
        IReadOnlyDictionary<uint, uint>? knownWordMasks = null)
    {
        if (bytes.Length is <= 0 or > 65536 || bytes.Length % 16 != 0)
            throw new ArgumentException("Native constant data must contain 1 to 4096 vectors.");
        int wordCount = bytes.Length / 4;
        if (knownWordMasks is not null && knownWordMasks.Keys.Any(word => word >= wordCount))
            throw new ArgumentException("Constant-buffer word mask leaves its buffer.");
        return new(Device.CreateBuffer(bytes, BindFlags.ConstantBuffer), (uint)bytes.Length, knownWordMasks);
    }

    /** The declared element format determines the byte stride of typed shader loads. */
    public NativeBufferResource CreateNativeBufferResource(ReadOnlySpan<byte> bytes, Format format)
    {
        int stride = format switch
        {
            Format.R8_UNorm => 1,
            Format.R32_Float or Format.R32_UInt or Format.R32_SInt => 4,
            Format.R32G32_Float or Format.R32G32_UInt or Format.R32G32_SInt => 8,
            Format.R32G32B32A32_Float or Format.R32G32B32A32_UInt or Format.R32G32B32A32_SInt => 16,
            _ => throw new NotSupportedException($"Typed buffer format {format} is unsupported.")
        };
        if (bytes.IsEmpty || bytes.Length % stride != 0)
            throw new ArgumentException("Typed buffer bytes do not fit the element format.");
        var buffer = Device.CreateBuffer(bytes, BindFlags.ShaderResource);
        try
        {
            return new(buffer, Device.CreateShaderResourceView(buffer,
                new ShaderResourceViewDescription(ShaderResourceViewDimension.Buffer,
                    format, 0, (uint)(bytes.Length / stride))));
        }
        catch { buffer.Dispose(); throw; }
    }

    /** Rewrites one vertex stream in place; the size must match and the buffer be default usage. */
    public void UpdateNativeVertexStream(NativeGpuGeometry geometry, int slot, ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(geometry.IsDisposed, geometry);
        var description = geometry.Streams[slot].Description;
        if (bytes.Length != description.ByteWidth) throw new ArgumentException("Native vertex stream update size changed.");
        if (description.Usage != ResourceUsage.Default)
            throw new InvalidOperationException("Vertex stream updates require a default-usage buffer.");
        Context.UpdateSubresource(bytes, geometry.Streams[slot]);
    }

    /** Overwrites a constant buffer's contents. */
    public void UpdateNativeConstantBuffer(NativeConstantBuffer buffer, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != buffer.Size) throw new ArgumentException("Native constant update size changed.");
        Context.UpdateSubresource(bytes, buffer.Buffer);
    }

    /** The caller chooses the depth clear value to match the supplied comparison state. */
    public void ClearNativeTargets(NativeRenderTargets targets, Vector4 clear, float depthValue, byte stencilValue = 0)
    {
        foreach (var view in targets.Views)
            Context.ClearRenderTargetView(view, new Color4(clear.X, clear.Y, clear.Z, clear.W));
        var flags = DepthStencilClearFlags.Depth | (targets.HasStencil ? DepthStencilClearFlags.Stencil : 0);
        Context.ClearDepthStencilView(targets.DepthView, flags, depthValue, stencilValue);
    }

    /** Readback returns the stored attachment value without a lighting or display transform. */
    public Vector4 ReadNativeTargetPixel(NativeRenderTargets targets, int target, int x, int y)
    {
        if ((uint)target >= targets.Textures.Count)
            throw new ArgumentOutOfRangeException(nameof(target));
        return ReadTexturePixel(targets.Textures[target], x, y);
    }

    /** Pixel readback preserves stored values and accepts uncompressed two-dimensional textures. */
    public Vector4 ReadTexturePixel(GpuTexture source, int x, int y)
    {
        if (source.Dimension != ShaderResourceViewDimension.Texture2D)
            throw new NotSupportedException("Pixel readback needs a two-dimensional texture.");
        if ((uint)x >= source.Width || (uint)y >= source.Height)
            throw new ArgumentOutOfRangeException(nameof(x));
        return ReadPixel((ID3D11Texture2D)source.Resource, source.View.Description.Format, x, y);
    }

    /** Depth readback returns the stored depth plane in X. */
    public Vector4 ReadNativeDepthPixel(NativeRenderTargets targets, int x, int y)
    {
        if ((uint)x >= targets.Width || (uint)y >= targets.Height)
            throw new ArgumentOutOfRangeException(nameof(x));
        return ReadPixel(targets.Depth, Format.Unknown, x, y);
    }

    /** One staging copy serves every requested point. */
    public Vector4[] ReadTexturePixels(GpuTexture source, IReadOnlyList<(int X, int Y)> points)
    {
        if (source.Dimension != ShaderResourceViewDimension.Texture2D)
            throw new NotSupportedException("Pixel readback needs a two-dimensional texture.");
        return ReadPixels((ID3D11Texture2D)source.Resource, source.View.Description.Format, source.Width, source.Height, points);
    }

    /** Reads several depth values with one staging copy. */
    public Vector4[] ReadNativeDepthPixels(NativeRenderTargets targets, IReadOnlyList<(int X, int Y)> points) =>
        ReadPixels(targets.Depth, Format.Unknown, targets.Width, targets.Height, points);

    private Vector4 ReadPixel(ID3D11Texture2D texture, Format format, int x, int y) =>
        ReadPixels(texture, format, int.MaxValue, int.MaxValue, [(x, y)])[0];

    private Vector4[] ReadPixels(ID3D11Texture2D texture, Format format, int width, int height,
        IReadOnlyList<(int X, int Y)> points)
    {
        foreach (var point in points)
            if (point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height)
                throw new ArgumentOutOfRangeException(nameof(points));
        var description = texture.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = Device.CreateTexture2D(description);
        Context.CopyResource(staging, texture);
        var mapped = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var results = new Vector4[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                (int x, int y) = points[i];
                results[i] = DecodePixel((byte*)mapped.DataPointer + y * mapped.RowPitch, x, description.Format, format);
            }
            return results;
        }
        finally { Context.Unmap(staging, 0); }
    }

    private static Vector4 DecodePixel(byte* row, int x, Format resourceFormat, Format viewFormat)
    {
        if (resourceFormat == Format.R32G32B32A32_Float)
            return ((Vector4*)row)[x];
        if (resourceFormat == Format.R32G32_Float)
            return new(((float*)row)[x * 2], ((float*)row)[x * 2 + 1], 0, 1);
        if (resourceFormat == Format.R16G16B16A16_UNorm)
        {
            ushort* unorm = (ushort*)row + x * 4;
            return new Vector4(unorm[0], unorm[1], unorm[2], unorm[3]) / 65535f;
        }
        if (resourceFormat == Format.R16G16B16A16_Float)
        {
            Half* value = (Half*)row + x * 4;
            return new((float)value[0], (float)value[1], (float)value[2], (float)value[3]);
        }
        if (resourceFormat == Format.R11G11B10_Float)
            return DecodeR11G11B10(((uint*)row)[x]);
        // Depth readback returns the depth plane only; the stencil plane is not decoded.
        if (resourceFormat == Format.R32G8X24_Typeless)
            return new(((float*)(row + x * 8))[0], 0, 0, 1);
        if (resourceFormat == Format.R32_Typeless)
            return new(((float*)row)[x], 0, 0, 1);
        if (resourceFormat == Format.R8G8_UNorm)
            return new(row[x * 2] / 255f, row[x * 2 + 1] / 255f, 0, 1);
        byte* pixel = row + x * 4;
        if (viewFormat == Format.R10G10B10A2_UNorm)
        {
            uint packed = ((uint*)row)[x];
            return new((packed & 1023) / 1023f, ((packed >> 10) & 1023) / 1023f,
                ((packed >> 20) & 1023) / 1023f, (packed >> 30) / 3f);
        }
        return viewFormat switch
        {
            Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb => new Vector4(pixel[0], pixel[1], pixel[2], pixel[3]) / 255,
            Format.B8G8R8A8_UNorm => new Vector4(pixel[2], pixel[1], pixel[0], pixel[3]) / 255,
            _ => throw new NotSupportedException($"Readback format {resourceFormat} is unsupported.")
        };
    }

    /** Packed floats carry a 5-bit exponent with 6, 6 and 5 mantissa bits and no sign. */
    public static Vector4 DecodeR11G11B10(uint packed)
    {
        static float Decode(uint bits, int mantissaBits)
        {
            uint mantissa = bits & ((1u << mantissaBits) - 1);
            uint exponent = bits >> mantissaBits;
            float scale = 1f / (1 << mantissaBits);
            if (exponent == 0) return MathF.ScaleB(mantissa * scale, -14);
            if (exponent == 31) return mantissa == 0 ? float.PositiveInfinity : float.NaN;
            return MathF.ScaleB(1 + mantissa * scale, (int)exponent - 15);
        }
        return new(Decode(packed & 0x7FF, 6), Decode((packed >> 11) & 0x7FF, 6), Decode(packed >> 22, 5), 1);
    }

    /** Validation runs before any native draw so absent externs cannot silently become zero. */
    public void DrawNative(NativeMaterialPipeline pipeline, NativeGpuMesh mesh,
        NativeDrawBindings bindings, NativeRenderTargets targets, uint instanceCount = 1)
    {
        ObjectDisposedException.ThrowIf(mesh.IsDisposed || mesh.Geometry.IsDisposed, mesh);
        var missing = pipeline.Validate(bindings, targets).ToList();
        foreach (var element in pipeline.Elements)
            if (element.Slot >= mesh.Streams.Length
                || (ulong)element.AlignedByteOffset + NativeVertexFormatBytes(element.Format) > mesh.Strides[element.Slot])
                missing.Add($"Input {element.SemanticName}{element.SemanticIndex} leaves its vertex stream.");
        if (instanceCount == 0) missing.Add("Native draw needs at least one instance.");
        if (missing.Count != 0) throw new InvalidOperationException(string.Join(" ", missing));
        ApplyNativeBindings(pipeline, bindings, targets);
        Context.IASetPrimitiveTopology(mesh.Topology);
        for (uint i = 0; i < mesh.Streams.Length; i++)
            Context.IASetVertexBuffer(i, mesh.Streams[i], mesh.Strides[i]);
        Context.IASetIndexBuffer(mesh.Indices, mesh.IndexFormat, 0);
        Context.DrawIndexedInstanced(mesh.IndexCount, instanceCount, mesh.FirstIndex, 0, 0);
        DrawCalls++;
    }

    /** System-generated vertices need no stream or index allocation. */
    public void DrawNativeVertices(NativeMaterialPipeline pipeline, NativeDrawBindings bindings,
        NativeRenderTargets targets, PrimitiveTopology topology, uint vertexCount, uint firstVertex = 0)
    {
        var missing = pipeline.Validate(bindings, targets).ToList();
        if (pipeline.Layout is not null || pipeline.Elements.Length != 0)
            missing.Add("Non-indexed native drawing requires a shader with no vertex-stream inputs.");
        if (topology is not (PrimitiveTopology.TriangleList or PrimitiveTopology.TriangleStrip))
            missing.Add("Native generated vertices require triangle-list or triangle-strip topology.");
        if (vertexCount < 3 || (topology == PrimitiveTopology.TriangleList && vertexCount % 3 != 0)
            || (ulong)firstVertex + vertexCount > uint.MaxValue)
            missing.Add("Native generated vertex range is invalid.");
        if (missing.Count != 0) throw new InvalidOperationException(string.Join(" ", missing));
        ApplyNativeBindings(pipeline, bindings, targets);
        Context.IASetPrimitiveTopology(topology);
        Context.IASetIndexBuffer(null, Format.Unknown, 0);
        Context.Draw(vertexCount, firstVertex);
        DrawCalls++;
    }

    /** Indexed and generated-vertex draws use the same explicit state and resource bindings. */
    private void ApplyNativeBindings(NativeMaterialPipeline pipeline, NativeDrawBindings bindings,
        NativeRenderTargets targets)
    {
        Context.OMSetRenderTargets(targets.Views, targets.DepthView);
        Context.RSSetViewport(new Viewport(targets.Width, targets.Height));
        Context.RSSetScissorRect(targets.Width, targets.Height);
        Context.RSSetState(pipeline.Rasterizer);
        Context.OMSetDepthStencilState(pipeline.Depth, bindings.StencilReference ?? 0);
        Context.OMSetBlendState(pipeline.Blend, new Color4(1, 1, 1, 1), uint.MaxValue);
        Context.IASetInputLayout(pipeline.Layout);
        Context.VSSetShader(pipeline.VertexShader);
        Context.PSSetShader(pipeline.PixelShader);
        foreach (var item in bindings.ConstantBuffers)
            if (item.Key.Stage == GameShaderStage.Vertex) Context.VSSetConstantBuffer(item.Key.Slot, item.Value.Buffer);
            else if (item.Key.Stage == GameShaderStage.Pixel) Context.PSSetConstantBuffer(item.Key.Slot, item.Value.Buffer);
        foreach (var item in bindings.Resources)
            if (item.Key.Stage == GameShaderStage.Vertex) Context.VSSetShaderResource(item.Key.Slot, item.Value);
            else if (item.Key.Stage == GameShaderStage.Pixel) Context.PSSetShaderResource(item.Key.Slot, item.Value);
        foreach (var item in bindings.Samplers)
            if (item.Key.Stage == GameShaderStage.Vertex) Context.VSSetSampler(item.Key.Slot, item.Value);
            else if (item.Key.Stage == GameShaderStage.Pixel) Context.PSSetSampler(item.Key.Slot, item.Value);
    }

    /** Adds draws issued outside this class to the frame's draw count. */
    public void CountDraws(int count) => DrawCalls += count;

    /** Return to the swap-chain target before drawing the menu or diagnostic geometry. */
    public void RestoreMainTarget()
    {
        Context.OMSetRenderTargets(colorView, depthView);
        Context.RSSetViewport(new Viewport(Width, Height));
        Context.RSSetScissorRect(Width, Height);
    }

    private static uint NativeVertexFormatBytes(Format format) => format switch
    {
        Format.R32G32B32A32_Float or Format.R32G32B32A32_UInt or Format.R32G32B32A32_SInt => 16,
        Format.R32G32B32_Float or Format.R32G32B32_UInt or Format.R32G32B32_SInt => 12,
        Format.R16G16B16A16_Float or Format.R16G16B16A16_UNorm or Format.R16G16B16A16_SNorm
            or Format.R16G16B16A16_UInt or Format.R16G16B16A16_SInt or Format.R32G32_Float
            or Format.R32G32_UInt or Format.R32G32_SInt => 8,
        Format.R8G8B8A8_UNorm or Format.R8G8B8A8_SNorm or Format.R8G8B8A8_UInt or Format.R8G8B8A8_SInt
            or Format.R16G16_Float or Format.R16G16_UNorm or Format.R16G16_SNorm
            or Format.R16G16_UInt or Format.R16G16_SInt or Format.R32_Float or Format.R32_UInt
            or Format.R32_SInt or Format.R10G10B10A2_UNorm or Format.R10G10B10A2_UInt => 4,
        Format.R8G8_UNorm or Format.R8G8_SNorm or Format.R8G8_UInt or Format.R8G8_SInt
            or Format.R16_Float or Format.R16_UNorm or Format.R16_SNorm
            or Format.R16_UInt or Format.R16_SInt => 2,
        Format.R8_UNorm or Format.R8_SNorm or Format.R8_UInt or Format.R8_SInt => 1,
        _ => throw new NotSupportedException($"Native vertex format {format} is unsupported.")
    };
}
