using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/**
 * Builds the cascade shadow mask as 0x7FF742EDE7B0 does with shadow_quality 2: a half-resolution mask cleared
 * to one, one cascade_shadow technique per cascade with blend 89, which subtracts, then the bilateral blur.
 */
public sealed class NativeShadowMask : IDisposable
{
    // Techniques 63 (poisson_rotated_8) and 65 (poisson_rotated_12); profile row 2 filters 6, 6, 7, 7 pick them.
    public const uint Rotated8MaterialTag = 0x80B357F9, Rotated12MaterialTag = 0x80B357FF;
    public const uint BlurMaterialTag = 0x80B35DB0;
    // Profile row 2 +12; 0x7FF742EDE630 copies it to dword_7FF743C96CE8, which fills extern 6 vector 4 X.
    public const int MaskMode = 1;
    public static IReadOnlyList<uint> CascadeTechniques { get; } = [63, 63, 65, 65];
    public const string Policy = "Sun shadows: shadow_quality 2 selects profile row 2. Four cascades fitted as 0x7FF742EEA500"
        + " fits them (1536, 1536, 1280 and 1024 texels; only cascade 3 square and snapped), drawn with the shadow_generate"
        + " states. Mask mode 1: technique 63 for cascades 0 and 1 and 65 for 2 and 3 write a half-resolution mask"
        + " against the half-resolution depth and min/max linear depth, then technique 299 blurs it in two passes."
        + " The viewer draws each cascade as a full-screen quad on the forward far plane instead of the native cascade"
        + " volume; the cascade fades in extern 6 vector 8 limit it in depth. SDSM is not run: the bias and scale"
        + " texture is (0, 0, 1, 1). The temporal blend with the previous mask (technique 67) is not run.";
    // The lighting pass word with forced blend byte 0xD9: blend 89 subtracts, depth 16 tests without writing.
    private const uint PassStates = 0x81819081, ForcedStates = 0xD9;
    // 0x7FF742EDE7B0 forces every state index to 1 before the blur.
    private const uint BlurStates = 0x81818181;
    private const string VertexDigest = "06E9081D14A7B71EC0A122FBA9E664A28E7EB43609517FA85C05FE7F125AC4E1";
    private const string Rotated8PixelDigest = "9D4A3C0ED4EEA7D4604B23F4B4027C9DF9C648731D3554E727BF26DEB0C7C75C";
    private const string Rotated12PixelDigest = "814E3F26DAC30BFBB995F6A59383C48E71F0D743A45BE5EBDD451639251C3743";
    private const string VertexProgram = "3F060547403E020E44173E0F00441F";
    private const string PixelProgram = "4C0049214C0149223F030747203F060147213F060047223E060C44063C06063C06070C34003C06063C"
        + "06070C040D430E3C0608430F3D060343103D060443113D060543123D060843153D060943163D0300431B3C0200340103431D3C02013402"
        + "03431E";
    private const string BlurVertexDigest = "14CBA200AE2FEDBBFEF309C9284BD605C359A72C14D1ED563AB0B1046431AAEA";
    private const string BlurPixelDigest = "5CEBD12A0E5D5DF89512E3D1954F86E2DB7949400BA6AEAB1803BDF52CDDCCEC";
    private const string BlurPixelProgram = "4C0049214C014922";
    // Filter radius in texels per cascade: dword_7FF743C97548, 4C, 50, then 54 is -1, which falls back to 44.
    private static readonly float[] FilterRadius = [2, 3, 5, 6];
    // Extern 6 vector 4 Y and Z are the per-tap biases dword_7FF743C97558 and 5C divided by 100000.
    private static readonly Vector4 BiasVector = new(MaskMode, 1e-5f, 5e-5f, 0);
    // Extern 6 vector 3 X is dword_7FF743C96CA4; Z is dword_7FF743C96CB0, the sharpening multiplier.
    private const float Vector3X = -50, Sharpen = 1;
    // dword_7FF743C951E0: the mask clears to one.
    private static readonly Vector4 Lit = Vector4.One;
    // 0x7FF742EFFA30 offsets: xmmword_7FF74391E200 for the first pass and E240 for the second.
    private static readonly Vector4 FirstOffset = new(2, 0, 1, 0), SecondOffset = new(0, 2, 0, 1);
    private static readonly short[] QuadIndices = [0, 1, 2, 2, 1, 3];
    // The quad sits on the forward far plane, so depth state 16 passes only where geometry wrote depth.
    private static readonly float[] QuadVertices = [-1, -1, 1, 1, -1, 1, -1, 1, 1, 1, 1, 1];
    private static readonly NativeColorTargetFormat MaskFormat = new(Format.R8G8_UNorm, Format.R8G8_UNorm, Format.R8G8_UNorm);

    private sealed record Technique(MaterialExecutionData Execution, NativeMaterialPipeline Pipeline);

    private readonly Dx11Renderer renderer;
    private readonly List<IDisposable> owned = [];
    private readonly Technique rotated8, rotated12;
    private readonly MaterialExecutionData blurExecution;
    private readonly NativeMaterialPipeline blurPipeline;
    private readonly NativeGpuMesh quad;
    private readonly NativeDrawBindings bindings = new(), blurBindings = new();
    private readonly NativeConstantBuffer vertexConstants, pixelConstants, vertexView, pixelView, blurConstants, blurScope, blurView;
    private readonly GpuTexture sdsmIdentity;
    private readonly TfxResource sdsmToken = new("sdsm bias and scale", ShaderResourceViewDimension.Texture2D),
        depthToken = new("depth_stencil_read", ShaderResourceViewDimension.Texture2D),
        minMaxToken = new("half-resolution min/max depth", ShaderResourceViewDimension.Texture2D),
        cascadeToken = new("cascade shadow depth", ShaderResourceViewDimension.Texture2D);
    private NativeRenderTargets? targets, blurTargets;
    private bool disposed;

    /** The blurred mask of the last frame: X shadows the smallest and Y the largest depth of each 2x2 block. */
    public GpuTexture? Mask => targets?.Textures[0];
    public NativeStateSelection States { get; }
    public NativeStateSelection BlurSelection { get; }

    public NativeShadowMask(Dx11Renderer renderer, GameMaterialReader reader, NativeRenderStateCatalog states)
    {
        this.renderer = renderer;
        try
        {
            var resolved = states.Resolve(PassStates, 0, ForcedStates, NativeDepthTable.Alternate);
            States = resolved.Selection;
            var blend = resolved.Blend.RenderTarget[0];
            if (States != new NativeStateSelection(89, 16, 1, 1) || blend.BlendOperation != BlendOperation.ReverseSubtract
                || blend.SourceBlend != Blend.One || blend.DestinationBlend != Blend.One || resolved.DepthStencil.StencilEnable)
                throw new NotSupportedException("Shadow mask pass states differ from their verified selection.");
            rotated8 = ReadTechnique(reader, Rotated8MaterialTag, Rotated8PixelDigest, resolved);
            rotated12 = ReadTechnique(reader, Rotated12MaterialTag, Rotated12PixelDigest, resolved);
            blurExecution = reader.ReadExecution(BlurMaterialTag);
            var blurMaterial = blurExecution.Material;
            if (blurMaterial.VertexShader is null || blurMaterial.PixelShader is null
                || Convert.ToHexString(SHA256.HashData(reader.ReadShaderBytecode(blurMaterial.VertexShader))) != BlurVertexDigest
                || Convert.ToHexString(SHA256.HashData(reader.ReadShaderBytecode(blurMaterial.PixelShader))) != BlurPixelDigest
                || Convert.ToHexString(blurExecution.Pixel.TfxBytecode) != BlurPixelProgram || blurExecution.Vertex.TfxBytecode.Length != 0
                || blurExecution.Pixel.ConstantTemplate.Length != 4 || blurMaterial.StateOverrides != 0 || blurMaterial.Textures.Count != 0
                || blurExecution.Pixel.Samplers.Count != 2)
                throw new NotSupportedException("Shadow mask blur differs from its verified contract.");
            var blurResolved = states.Resolve(BlurStates, 0, BlurStates, NativeDepthTable.Alternate);
            BlurSelection = blurResolved.Selection;
            if (BlurSelection != new NativeStateSelection(1, 1, 1, 1) || blurResolved.DepthStencil.DepthEnable
                || blurResolved.Blend.RenderTarget[0].BlendEnable)
                throw new NotSupportedException("Shadow mask blur states differ from their verified selection.");
            blurPipeline = Own(renderer.CreateNativePipeline(reader, blurMaterial, [], blurResolved.Rasterizer,
                blurResolved.DepthStencil, blurResolved.Blend));
            quad = Own(renderer.CreateNativeMesh([new(MemoryMarshal.AsBytes(QuadVertices.AsSpan()).ToArray(), 12)],
                MemoryMarshal.AsBytes(QuadIndices.AsSpan()), Format.R16_UInt, 0, 6, PrimitiveTopology.TriangleList));
            vertexConstants = Own(renderer.CreateNativeConstantBuffer(new byte[35 * 16]));
            pixelConstants = Own(renderer.CreateNativeConstantBuffer(new byte[31 * 16]));
            vertexView = Own(renderer.CreateNativeConstantBuffer(new byte[10 * 16],
                new Dictionary<uint, uint> { [36] = uint.MaxValue, [37] = 0, [38] = 0, [39] = 0 }));
            pixelView = Own(renderer.CreateNativeConstantBuffer(new byte[13 * 16]));
            blurConstants = Own(renderer.CreateNativeConstantBuffer(new byte[4 * 16]));
            blurScope = Own(renderer.CreateNativeConstantBuffer(new byte[8 * 16]));
            blurView = Own(renderer.CreateNativeConstantBuffer(new byte[10 * 16],
                new Dictionary<uint, uint> { [36] = uint.MaxValue, [37] = 0, [38] = 0, [39] = 0 }));
            // Fixed slot 67, the SDSM-off bias and scale: xmmword_7FF7439023F0.
            sdsmIdentity = Own(renderer.CreateTexture(1, 1, Format.R32G32B32A32_Float,
                MemoryMarshal.AsBytes(new[] { new Vector4(0, 0, 1, 1) }.AsSpan()), 16));
            bindings.ConstantBuffers[new(GameShaderStage.Vertex, 0)] = vertexConstants;
            bindings.ConstantBuffers[new(GameShaderStage.Pixel, 0)] = pixelConstants;
            bindings.ConstantBuffers[new(GameShaderStage.Vertex, 12)] = vertexView;
            bindings.ConstantBuffers[new(GameShaderStage.Pixel, 12)] = pixelView;
            blurBindings.ConstantBuffers[new(GameShaderStage.Pixel, 0)] = blurConstants;
            blurBindings.ConstantBuffers[new(GameShaderStage.Pixel, 11)] = blurScope;
            blurBindings.ConstantBuffers[new(GameShaderStage.Vertex, 12)] = blurView;
        }
        catch { Dispose(); throw; }
    }

    private Technique ReadTechnique(GameMaterialReader reader, uint tag, string pixelDigest, NativeRenderStateDescriptions resolved)
    {
        var execution = reader.ReadExecution(tag);
        var material = execution.Material;
        if (material.VertexShader is null || material.PixelShader is null
            || Convert.ToHexString(SHA256.HashData(reader.ReadShaderBytecode(material.VertexShader))) != VertexDigest
            || Convert.ToHexString(SHA256.HashData(reader.ReadShaderBytecode(material.PixelShader))) != pixelDigest)
            throw new NotSupportedException("Shadow mask bytecode differs from its verified contract.");
        if (Convert.ToHexString(execution.Vertex.TfxBytecode) != VertexProgram
            || Convert.ToHexString(execution.Pixel.TfxBytecode) != PixelProgram
            || execution.Vertex.ConstantTemplate.Length != 35 || execution.Pixel.ConstantTemplate.Length != 31)
            throw new NotSupportedException("Shadow mask TFX programs differ from their verified contract.");
        if (material.StateOverrides != 0 || material.Textures.Count != 0 || execution.Pixel.Samplers.Count != 2)
            throw new NotSupportedException("Shadow mask material now carries overrides, textures or other samplers.");
        return new(execution, Own(renderer.CreateNativePipeline(reader, material,
            [new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0)],
            resolved.Rasterizer, resolved.DepthStencil, resolved.Blend)));
    }

    private T Own<T>(T item) where T : IDisposable { owned.Add(item); return item; }

    /** The SDSM-off bias and scale texture; shadow casters read it at vertex t2. */
    public GpuTexture SdsmIdentity => sdsmIdentity;

    /**
     * Clears the half-resolution mask to one, subtracts each cascade's shadow, then blurs it.
     * @param cascadeDepth Each cascade's depth map, in cascade order.
     * @param downsample The half-resolution depth and min/max linear depth of this frame.
     * @param depthRead The full-resolution depth; mask mode 1 binds but does not read it.
     */
    public GpuTexture Render(IReadOnlyList<NativeShadowCascade> cascades, IReadOnlyList<ID3D11ShaderResourceView> cascadeDepth,
        NativeDepthDownsample downsample, ID3D11ShaderResourceView depthRead, int width, int height,
        NativeCameraScopeConstants camera, Matrix4x4 worldToView, Matrix4x4 projection, NativeFrameDepthInputs depth)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (cascades.Count != cascadeDepth.Count) throw new ArgumentException("Every cascade needs its depth map.");
        if (camera.Vertex.Length < 9 || camera.Pixel.Length != 13)
            throw new ArgumentException("The shadow mask needs nine vertex view rows and thirteen pixel view rows.");
        var half = downsample.HalfDepth ?? throw new InvalidOperationException("The depth downsample has not run.");
        var minMax = downsample.MinMax!.Texture;
        if (targets is null || !ReferenceEquals(targets.Depth, half.Depth) || targets.Width != half.Width || targets.Height != half.Height)
        {
            targets?.Dispose(); blurTargets?.Dispose();
            targets = null; blurTargets = null;
            targets = renderer.CreateNativeTargetsOnDepth([MaskFormat], half);
            blurTargets = renderer.CreateNativeTargetsOnDepth([MaskFormat], half);
        }
        if (!Matrix4x4.Invert(worldToView, out var viewToWorld)) throw new ArgumentException("The main view is singular.");
        var vertexRows = new Vector4[10];
        Array.Copy(camera.Vertex, vertexRows, 9);
        vertexRows[9] = new(1, 0, 0, 0);
        renderer.UpdateNativeConstantBuffer(vertexView, MemoryMarshal.AsBytes(vertexRows.AsSpan()));
        renderer.UpdateNativeConstantBuffer(pixelView, MemoryMarshal.AsBytes(camera.Pixel.AsSpan()));
        var linear = NativeDepthReconstruction.Create(depth);
        try
        {
            renderer.ClearNativeColorTargets(targets, Lit);
            for (int i = 0; i < cascades.Count; i++)
            {
                var cascade = cascades[i];
                var technique = CascadeTechniques[i] == 63 ? rotated8 : rotated12;
                // Extern 2 is the main view's, so floats 0 and 1 hold the full-resolution size.
                var tfx = NativeViewInputs.CreateCameraInputs(checked((ushort)width), checked((ushort)height), worldToView, projection);
                tfx.Vectors[new(3, 0)] = linear;
                tfx.Matrices[new(15, 0)] = Matrix4x4.Identity;
                tfx.Matrices[new(6, 12)] = viewToWorld * cascade.ViewProjection;
                tfx.Floats[new(6, 6)] = cascade.Resolution;
                tfx.Floats[new(6, 7)] = cascade.Resolution;
                tfx.Floats[new(6, 8)] = 1;
                tfx.Vectors[new(6, 3)] = new(Vector3X, 0, Sharpen, FilterRadius[Math.Min(i, FilterRadius.Length - 1)]);
                tfx.Vectors[new(6, 4)] = BiasVector;
                tfx.Vectors[new(6, 5)] = Vector4.Zero;
                tfx.Vectors[new(6, 8)] = Fades(cascades, i);
                tfx.Vectors[new(6, 9)] = new(cascade.Near - 0.001f, cascade.BlendFar + 0.001f, 0, 0);
                tfx.Resources[new(6, 5)] = sdsmToken;
                tfx.Resources[new(3, 7)] = depthToken;
                tfx.Resources[new(6, 1)] = minMaxToken;
                tfx.Resources[new(6, 0)] = cascadeToken;
                var vertex = TfxEvaluator.Evaluate(technique.Execution.Vertex, tfx);
                var pixel = TfxEvaluator.Evaluate(technique.Execution.Pixel, tfx);
                vertex.RequireComplete(); pixel.RequireComplete();
                renderer.UpdateNativeConstantBuffer(vertexConstants, MemoryMarshal.AsBytes(vertex.Constants.AsSpan()));
                renderer.UpdateNativeConstantBuffer(pixelConstants, MemoryMarshal.AsBytes(pixel.Constants.AsSpan()));
                bindings.MaterialEvaluations[GameShaderStage.Vertex] = vertex;
                bindings.MaterialEvaluations[GameShaderStage.Pixel] = pixel;
                var views = new Dictionary<TfxResource, ID3D11ShaderResourceView>
                {
                    [sdsmToken] = sdsmIdentity.View, [depthToken] = depthRead, [minMaxToken] = minMax.View,
                    [cascadeToken] = cascadeDepth[i]
                };
                bindings.ApplyTfxResources(vertex, views);
                bindings.ApplyTfxResources(pixel, views);
                foreach (var item in pixel.Samplers)
                    if (!bindings.Samplers.ContainsKey(item.Key))
                        bindings.Samplers[item.Key] = Own(renderer.Device.CreateSamplerState(item.Value.Description));
                renderer.DrawNative(technique.Pipeline, quad, bindings, targets);
            }
            for (uint slot = 0; slot < 3; slot++) renderer.Context.PSSetShaderResource(slot, null!);
            renderer.Context.VSSetShaderResource(0, null!);
            Blur(minMax, linear);
        }
        finally
        {
            for (uint slot = 0; slot < 3; slot++) renderer.Context.PSSetShaderResource(slot, null!);
            renderer.Context.VSSetShaderResource(0, null!);
            renderer.RestoreMainTarget();
        }
        return targets.Textures[0];
    }

    /**
     * Technique 299 as 0x7FF742EFFA30 runs it: across into the second mask, then down back into the first.
     * CB11 holds rows 4, 5, 6, 11 to 15 of the pass block (0x7FF742F0E420).
     */
    private void Blur(GpuTexture minMax, Vector4 linear)
    {
        var pixel = TfxEvaluator.Evaluate(blurExecution.Pixel, new TfxInputs());
        pixel.RequireComplete();
        renderer.UpdateNativeConstantBuffer(blurConstants, MemoryMarshal.AsBytes(pixel.Constants.AsSpan()));
        blurBindings.MaterialEvaluations[GameShaderStage.Pixel] = pixel;
        foreach (var item in pixel.Samplers)
            if (!blurBindings.Samplers.ContainsKey(item.Key))
                blurBindings.Samplers[item.Key] = Own(renderer.Device.CreateSamplerState(item.Value.Description));
        var view = new Vector4[10];
        view[9] = new(1, 0, 0, 0);
        renderer.UpdateNativeConstantBuffer(blurView, MemoryMarshal.AsBytes(view.AsSpan()));
        foreach (bool first in new[] { true, false })
        {
            var source = first ? targets!.Textures[0] : blurTargets!.Textures[0];
            var output = first ? blurTargets! : targets!;
            var sourceSize = Size(source.Width, source.Height);
            // The first pass scales the offset by (1/w, 1/w, 1/w, 1/h), the second by (1/h, 1/h, 1/h, 1/w).
            var scale = first ? new Vector4(sourceSize.Z, sourceSize.Z, sourceSize.Z, sourceSize.W)
                : new Vector4(sourceSize.W, sourceSize.W, sourceSize.W, sourceSize.Z);
            var rows = new[]
            {
                Size(output.Width, output.Height), sourceSize, Size(minMax.Width, minMax.Height), Vector4.Zero,
                (first ? FirstOffset : SecondOffset) * scale, Vector4.Zero, Vector4.Zero, new(linear.X, linear.Y, 0, 0)
            };
            renderer.UpdateNativeConstantBuffer(blurScope, MemoryMarshal.AsBytes(rows.AsSpan()));
            blurBindings.Resources[new(GameShaderStage.Pixel, 0)] = source.View;
            blurBindings.Resources[new(GameShaderStage.Pixel, 1)] = minMax.View;
            renderer.DrawNativeVertices(blurPipeline, blurBindings, output, PrimitiveTopology.TriangleStrip, 4);
            renderer.Context.PSSetShaderResource(0, null!);
        }
    }

    private static Vector4 Size(int width, int height) => new(width, height, 1f / width, 1f / height);

    /**
     * Extern 6 vector 8 of 0x7FF742F0AC20 in linear depth: X and Y fade the cascade out, Z and W fade it in.
     * A cascade fades out from its split to its blend far plane, where the next one fades in.
     * The last fades out over the last tenth of the shadow distance.
     */
    public static Vector4 Fades(IReadOnlyList<NativeShadowCascade> cascades, int index)
    {
        var cascade = cascades[index];
        bool last = index == cascades.Count - 1;
        float end = last ? NativeShadowCascades.Distance : cascade.BlendFar;
        float start = last ? NativeShadowCascades.LastFadeStart : cascade.Split;
        var fades = end > start ? new Vector4(-1 / (end - start), end / (end - start), 0, 1) : new Vector4(0, 1, 0, 1);
        if (index > 0)
        {
            var previous = cascades[index - 1];
            float inEnd = previous.BlendFar, inStart = previous.Split;
            if (inEnd > inStart) { fades.Z = 1 / (inEnd - inStart); fades.W = -inStart / (inEnd - inStart); }
        }
        return fades;
    }

    /** Releases the GPU resources this pass owns. */
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        targets?.Dispose(); blurTargets?.Dispose();
        for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        owned.Clear();
    }
}
