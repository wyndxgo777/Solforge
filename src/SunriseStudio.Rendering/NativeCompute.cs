using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/** A two-dimensional texture a compute shader writes through a typed UAV and later passes read. */
public sealed class NativeUnorderedTexture : IDisposable
{
    public GpuTexture Texture { get; }
    internal ID3D11UnorderedAccessView View { get; }

    internal NativeUnorderedTexture(GpuTexture texture, ID3D11UnorderedAccessView view)
    {
        Texture = texture; View = view;
    }

    /** Releases the texture and its unordered-access view. */
    public void Dispose()
    {
        View.Dispose();
        Texture.Dispose();
    }
}

/** One material's compute shader, with the slots its declarations name. */
public sealed class NativeComputePipeline : IDisposable
{
    internal ID3D11ComputeShader Shader { get; }
    public uint MaterialTag { get; }
    /** SHA-256 of the compute bytecode. */
    public string Digest { get; }
    internal IReadOnlyList<(uint Slot, uint Vectors)> Constants { get; }
    internal IReadOnlyList<uint> Resources { get; }
    internal IReadOnlyList<uint> UnorderedViews { get; }

    internal NativeComputePipeline(ID3D11Device device, GameMaterialReader reader, GameMaterialInfo material)
    {
        var cs = material.ComputeShader ?? throw new InvalidDataException("Native material has no compute shader.");
        if (material.VertexShader is not null || material.PixelShader is not null)
            throw new NotSupportedException("A compute material also carries graphics stages.");
        var constants = new List<(uint, uint)>();
        var resources = new List<uint>();
        var views = new List<uint>();
        foreach (string declaration in cs.Declarations)
        {
            var cb = Regex.Match(declaration, @"^dcl_constantbuffer CB(\d+)\[(\d+)\]");
            if (cb.Success) constants.Add((uint.Parse(cb.Groups[1].Value), uint.Parse(cb.Groups[2].Value)));
            var uav = Regex.Match(declaration, @"^dcl_uav_\w+ u(\d+)");
            if (uav.Success) views.Add(uint.Parse(uav.Groups[1].Value));
            if (declaration.StartsWith("dcl_sampler", StringComparison.Ordinal))
                throw new NotSupportedException("Native compute does not bind samplers.");
        }
        resources.AddRange(cs.TextureSlots.Select(x => x.Slot));
        byte[] code = reader.ReadShaderBytecode(cs);
        Digest = Convert.ToHexString(SHA256.HashData(code));
        Shader = device.CreateComputeShader(code);
        MaterialTag = material.Tag;
        Constants = constants; Resources = resources; UnorderedViews = views;
    }

    /** Releases the compute shader. */
    public void Dispose() => Shader.Dispose();
}

/** Resources one dispatch binds, by compute slot. */
public sealed class NativeComputeBindings
{
    public Dictionary<uint, NativeConstantBuffer> ConstantBuffers { get; } = [];
    public Dictionary<uint, ID3D11ShaderResourceView> Resources { get; } = [];
    public Dictionary<uint, NativeUnorderedTexture> UnorderedViews { get; } = [];
}

/** Compute-pipeline part of Dx11Renderer. */
public sealed partial class Dx11Renderer
{
    /** Builds a compute pipeline from a compute-only material. */
    public NativeComputePipeline CreateNativeComputePipeline(GameMaterialReader reader, GameMaterialInfo material) =>
        new(Device, reader, material);

    /** A texture with shader-read and typed UAV views; the format must support typed UAV stores. */
    public NativeUnorderedTexture CreateNativeUnorderedTexture(int width, int height, Format format)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Native compute texture size is invalid.");
        var texture = Device.CreateTexture2D(format, (uint)width, (uint)height, mipLevels: 1,
            bindFlags: BindFlags.ShaderResource | BindFlags.UnorderedAccess);
        GpuTexture? wrapped = null;
        try
        {
            wrapped = new(texture, Device.CreateShaderResourceView(texture,
                new ShaderResourceViewDescription(ShaderResourceViewDimension.Texture2D, format, 0, 1)), width, height);
            return new(wrapped, Device.CreateUnorderedAccessView(texture,
                new UnorderedAccessViewDescription(UnorderedAccessViewDimension.Texture2D, format)));
        }
        catch
        {
            if (wrapped is not null) wrapped.Dispose(); else texture.Dispose();
            throw;
        }
    }

    /**
     * Runs one compute dispatch; every declared slot must be bound. Slots are cleared afterwards so the
     * written textures can be read by later passes.
     */
    public void DispatchNative(NativeComputePipeline pipeline, NativeComputeBindings bindings, uint x, uint y, uint z = 1)
    {
        var missing = new List<string>();
        foreach (var (slot, vectors) in pipeline.Constants)
            if (!bindings.ConstantBuffers.TryGetValue(slot, out var buffer) || buffer.Size < vectors * 16)
                missing.Add($"Compute CB{slot} needs {vectors} vectors.");
        foreach (uint slot in pipeline.Resources)
            if (!bindings.Resources.ContainsKey(slot)) missing.Add($"Compute resource t{slot} is missing.");
        foreach (uint slot in pipeline.UnorderedViews)
            if (!bindings.UnorderedViews.ContainsKey(slot)) missing.Add($"Compute view u{slot} is missing.");
        if (x == 0 || y == 0 || z == 0) missing.Add("A dispatch needs at least one group.");
        if (missing.Count != 0) throw new InvalidOperationException(string.Join(" ", missing));
        // A texture bound as a render target cannot also be a UAV.
        Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
        Context.CSSetShader(pipeline.Shader);
        foreach (var item in bindings.ConstantBuffers) Context.CSSetConstantBuffer(item.Key, item.Value.Buffer);
        foreach (var item in bindings.Resources) Context.CSSetShaderResource(item.Key, item.Value);
        foreach (var item in bindings.UnorderedViews) Context.CSSetUnorderedAccessView(item.Key, item.Value.View);
        Context.Dispatch(x, y, z);
        foreach (var item in bindings.Resources) Context.CSSetShaderResource(item.Key, null!);
        foreach (var item in bindings.UnorderedViews) Context.CSSetUnorderedAccessView(item.Key, null!);
        Context.CSSetShader(null);
    }
}
