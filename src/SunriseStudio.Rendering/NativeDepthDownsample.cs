using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/**
 * The half-resolution depth targets 0x7FF742E9E020 builds after the G-buffer: technique 216 writes the
 * half-resolution depth (view +0x230) and technique 603 the min/max linear depth (view +0x238) and the
 * quarter-resolution depth tiles (view +0x244).
 */
public sealed class NativeDepthDownsample : IDisposable
{
    public const uint DepthMaterialTag = 0x80B35A0C, MinMaxMaterialTag = 0x80B9E326;
    public const string Policy = "Depth downsample: technique 216 (downsample_depth_buffer) writes the half-resolution depth"
        + " and technique 603 (uber_depth_default) the half-resolution min/max linear depth and the quarter-resolution"
        + " tiles, as 0x7FF742E9E020 runs them. The game runs technique 606, which adds the SDSM bounds; the viewer"
        + " does not run SDSM. Technique 216 keeps the smallest raw depth, which is the nearest one in the viewer's"
        + " forward depth and the farthest one in the game's reversed depth.";
    // Technique 216 runs with every state index forced to 1 and depth byte 0xA2 (0x7FF742F12D30).
    private const uint PassStates = 0x81818181, ForcedStates = 0x0000A200;
    private const string DepthVertexDigest = "14CBA200AE2FEDBBFEF309C9284BD605C359A72C14D1ED563AB0B1046431AAEA";
    private const string DepthPixelDigest = "E1502304627BE9A775825B1C9126408A1FF02842F5DF85CA149FA69D8A0B43FC";
    private const string DepthPixelProgram = "3F200C47203D200743013D20084302";
    private const string MinMaxProgram = "3F550047A04155064AA04155084AA13D550543003D55074302";
    private const string MinMaxDigest = "E9034EF712BF6E7360EA173B22E07EF0B89BD9FBA8634551A3A50440902FD88E";
    // Typed UAV formats; the game's target formats are not read from the binary.
    private const Format MinMaxFormat = Format.R32G32_Float, TileFormat = Format.R32G32B32A32_Float;

    private readonly Dx11Renderer renderer;
    private readonly List<IDisposable> owned = [];
    private readonly MaterialExecutionData depthExecution;
    private readonly NativeMaterialPipeline depthPipeline;
    private readonly NativeComputePipeline minMaxPipeline;
    private readonly NativeDrawBindings depthBindings = new();
    private readonly NativeComputeBindings minMaxBindings = new();
    private readonly NativeConstantBuffer depthConstants, depthView, minMaxConstants;
    private readonly TfxResource sourceToken = new("full-resolution depth", ShaderResourceViewDimension.Texture2D);
    private bool disposed;

    /** View +0x230: the half-resolution depth; mask mode 1 tests against it. */
    public NativeRenderTargets? HalfDepth { get; private set; }
    /** View +0x238: X is the smallest and Y the largest linear depth of each 2x2 block. */
    public NativeUnorderedTexture? MinMax { get; private set; }
    /** View +0x244: per 4x4 block the largest, smallest and average linear depth and a coverage ratio. */
    public NativeUnorderedTexture? Tiles { get; private set; }
    public NativeStateSelection States { get; }

    public NativeDepthDownsample(Dx11Renderer renderer, GameMaterialReader reader, NativeRenderStateCatalog states)
    {
        this.renderer = renderer;
        try
        {
            depthExecution = reader.ReadExecution(DepthMaterialTag);
            var material = depthExecution.Material;
            if (material.VertexShader is null || material.PixelShader is null
                || Convert.ToHexString(SHA256.HashData(reader.ReadShaderBytecode(material.VertexShader))) != DepthVertexDigest
                || Convert.ToHexString(SHA256.HashData(reader.ReadShaderBytecode(material.PixelShader))) != DepthPixelDigest
                || Convert.ToHexString(depthExecution.Pixel.TfxBytecode) != DepthPixelProgram
                || depthExecution.Vertex.TfxBytecode.Length != 0 || depthExecution.Pixel.ConstantTemplate.Length != 3
                || material.StateOverrides != 0 || material.Textures.Count != 0)
                throw new NotSupportedException("Depth downsample differs from its verified contract.");
            var resolved = states.Resolve(PassStates, material.StateOverrides, ForcedStates, NativeDepthTable.Alternate);
            States = resolved.Selection;
            if (!resolved.DepthStencil.DepthEnable || resolved.DepthStencil.DepthFunc != ComparisonFunction.Always
                || resolved.DepthStencil.DepthWriteMask != DepthWriteMask.All || resolved.DepthStencil.StencilEnable)
                throw new NotSupportedException($"Depth downsample states {States} do not write depth unconditionally.");
            depthPipeline = Own(renderer.CreateNativePipeline(reader, material, [], resolved.Rasterizer,
                resolved.DepthStencil, resolved.Blend, Dx11Renderer.NoColorTargets));
            var compute = reader.ReadExecution(MinMaxMaterialTag);
            minMaxPipeline = Own(renderer.CreateNativeComputePipeline(reader, compute.Material));
            // The program binds extern 85 resource 0 at t0, views 6 and 8 at u0 and u1, and vectors 5 and 7 at rows 0 and 2.
            if (minMaxPipeline.Digest != MinMaxDigest || Convert.ToHexString(compute.Compute.TfxBytecode) != MinMaxProgram
                || compute.Compute.ConstantTemplate.Length != 3 || compute.Compute.ConstantTemplate.Any(row => row != Vector4.Zero))
                throw new NotSupportedException("Min/max depth compute differs from its verified contract.");
            depthConstants = Own(renderer.CreateNativeConstantBuffer(new byte[3 * 16]));
            depthView = Own(renderer.CreateNativeConstantBuffer(new byte[10 * 16],
                new Dictionary<uint, uint> { [36] = uint.MaxValue, [37] = 0, [38] = 0, [39] = 0 }));
            minMaxConstants = Own(renderer.CreateNativeConstantBuffer(new byte[3 * 16]));
            depthBindings.ConstantBuffers[new(GameShaderStage.Pixel, 0)] = depthConstants;
            depthBindings.ConstantBuffers[new(GameShaderStage.Vertex, 12)] = depthView;
            minMaxBindings.ConstantBuffers[0] = minMaxConstants;
        }
        catch { Dispose(); throw; }
    }

    private T Own<T>(T item) where T : IDisposable { owned.Add(item); return item; }

    /**
     * Builds all three targets from the full-resolution depth.
     * @param linear Extern 3 vector 0: view depth is 1 / (x + depth * y).
     * @param cotangent One over the tangent of half the vertical field of view; the shaders do not read it.
     */
    public void Render(ID3D11ShaderResourceView depth, int width, int height, Vector4 linear, float cotangent)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int halfWidth = (width + 1) / 2, halfHeight = (height + 1) / 2;
        if (HalfDepth is null || HalfDepth.Width != halfWidth || HalfDepth.Height != halfHeight)
        {
            HalfDepth?.Dispose(); MinMax?.Dispose(); Tiles?.Dispose();
            HalfDepth = null; MinMax = null; Tiles = null;
            HalfDepth = renderer.CreateNativeTargets(halfWidth, halfHeight, Array.Empty<NativeColorTargetFormat>(), Format.D32_Float, true);
            MinMax = renderer.CreateNativeUnorderedTexture(halfWidth, halfHeight, MinMaxFormat);
            Tiles = renderer.CreateNativeUnorderedTexture((width + 3) / 4, (height + 3) / 4, TileFormat);
        }
        // Extern 32 rows 7 and 8 (0x7FF742F12F20): target and source size, then their reciprocals.
        var tfx = new TfxInputs();
        tfx.Resources[new(32, 12)] = sourceToken;
        tfx.Vectors[new(32, 7)] = new(halfWidth, halfHeight, 1f / halfWidth, 1f / halfHeight);
        tfx.Vectors[new(32, 8)] = new(width, height, 1f / width, 1f / height);
        var pixel = TfxEvaluator.Evaluate(depthExecution.Pixel, tfx);
        pixel.RequireComplete();
        renderer.UpdateNativeConstantBuffer(depthConstants, MemoryMarshal.AsBytes(pixel.Constants.AsSpan()));
        depthBindings.MaterialEvaluations[GameShaderStage.Pixel] = pixel;
        depthBindings.ApplyTfxResources(pixel, new Dictionary<TfxResource, ID3D11ShaderResourceView> { [sourceToken] = depth });
        var view = new Vector4[10];
        // Row 9 X is the quad depth; the pass writes oDepth, so any value inside the clip range serves.
        view[9] = new(1, 0, 0, 0);
        renderer.UpdateNativeConstantBuffer(depthView, MemoryMarshal.AsBytes(view.AsSpan()));
        // The compute program writes extern 85 vector 5 (the linear depth row) to row 0 and vector 7 (the source size) to row 2.
        var rows = new[] { new Vector4(linear.X, linear.Y, cotangent, 1), Vector4.Zero, new Vector4(0, 0, width, height) };
        renderer.UpdateNativeConstantBuffer(minMaxConstants, MemoryMarshal.AsBytes(rows.AsSpan()));
        try
        {
            renderer.ClearNativeTargets(HalfDepth, Vector4.Zero, 1);
            renderer.DrawNativeVertices(depthPipeline, depthBindings, HalfDepth, PrimitiveTopology.TriangleStrip, 4);
            renderer.Context.PSSetShaderResource(0, null!);
            minMaxBindings.Resources[0] = depth;
            minMaxBindings.UnorderedViews[0] = MinMax!;
            minMaxBindings.UnorderedViews[1] = Tiles!;
            // Each 8x8 group covers 16x16 source pixels.
            renderer.DispatchNative(minMaxPipeline, minMaxBindings, (uint)(width + 15) >> 4, (uint)(height + 15) >> 4);
        }
        finally { renderer.RestoreMainTarget(); }
    }

    /** Releases the GPU resources this pass owns. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        HalfDepth?.Dispose(); MinMax?.Dispose(); Tiles?.Dispose();
        for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        owned.Clear();
    }
}
