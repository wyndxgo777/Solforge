using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/** Final-combine consumes a supplied color buffer; scene lighting must produce that buffer first. */
public sealed class NativeFinalCombine : IDisposable
{
    private sealed record Pass(NativeMaterialPipeline Pipeline, NativeDrawBindings Bindings,
        TfxEvaluation PixelEvaluation);
    private readonly Dx11Renderer renderer;
    private readonly List<IDisposable> owned = [];
    private readonly Pass film, noFilm;
    private readonly NativeConstantBuffer vertexView, pixelView;
    private readonly TfxResource sourceToken = new("final_combine input", ShaderResourceViewDimension.Texture2D);
    private NativeRenderTargets? targets;
    private bool disposed;
    public GpuTexture? Output => targets?.Textures[0];

    /** Hash checks bound the view rows this specialized pass is allowed to supply. */
    public NativeFinalCombine(Dx11Renderer renderer, GameMaterialReader reader)
    {
        this.renderer = renderer;
        try
        {
            vertexView = Own(renderer.CreateNativeConstantBuffer(new byte[10 * 16]));
            pixelView = Own(renderer.CreateNativeConstantBuffer(new byte[13 * 16]));
            film = CreatePass(reader, 0x80B35AB8,
                "B292903B12C10AB93127BC91B641EE8658261F1D49095B9A4A2F736433F14318");
            noFilm = CreatePass(reader, 0x80B35ABB,
                "D4E3B8EF74892F1190DCCEFF49C100DC08A57F2E34837A6438BE4B5CDAF59E4F");
        }
        catch { Dispose(); throw; }
    }

    private T Own<T>(T item) where T : IDisposable { owned.Add(item); return item; }

    /** Authored TFX supplies the sampler and texture slot; no bindings are inferred from their names. */
    private Pass CreatePass(GameMaterialReader reader, uint tag, string pixelDigest)
    {
        var execution = reader.ReadExecution(tag);
        var material = execution.Material;
        if (material.VertexShader is null || material.PixelShader is null ||
            Convert.ToHexString(SHA256.HashData(reader.ReadShaderBytecode(material.VertexShader))) !=
                "14CBA200AE2FEDBBFEF309C9284BD605C359A72C14D1ED563AB0B1046431AAEA" ||
            Convert.ToHexString(SHA256.HashData(reader.ReadShaderBytecode(material.PixelShader))) != pixelDigest)
            throw new NotSupportedException("Final-combine bytecode differs from its verified view contract.");
        var inputs = new TfxInputs(); inputs.Resources.Add(new(0x25, 0), sourceToken);
        var bindings = new NativeDrawBindings();
        TfxEvaluation pixel = null!;
        foreach (var stage in new[] { execution.Vertex, execution.Pixel })
        {
            var evaluation = TfxEvaluator.Evaluate(stage, inputs);
            evaluation.RequireComplete();
            bindings.MaterialEvaluations.Add(stage.Stage, evaluation);
            if (stage.Stage == GameShaderStage.Pixel) pixel = evaluation;
            if (evaluation.Constants.Length != 0)
                bindings.ConstantBuffers.Add(new(stage.Stage, 0), Own(renderer.CreateNativeConstantBuffer(
                    MemoryMarshal.AsBytes(evaluation.Constants.AsSpan()))));
            foreach (var sampler in evaluation.Samplers)
                bindings.Samplers.Add(sampler.Key, Own(renderer.Device.CreateSamplerState(sampler.Value.Description)));
        }
        bindings.ConstantBuffers.Add(new(GameShaderStage.Vertex, 12), vertexView);
        bindings.ConstantBuffers.Add(new(GameShaderStage.Pixel, 12), pixelView);
        var pipeline = Own(renderer.CreateNativePipeline(reader, material, [],
            new RasterizerDescription(CullMode.None, FillMode.Solid),
            new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Always),
            new BlendDescription(Blend.One, Blend.Zero)));
        return new(pipeline, bindings, pixel);
    }

    /** The original quad VS uses only an explicit clip depth; the PS reads reciprocal viewport size. */
    public GpuTexture Render(GpuTexture source, bool applyFilmCurve = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (source.View.NativePointer == IntPtr.Zero || source.Dimension != ShaderResourceViewDimension.Texture2D)
            throw new ArgumentException("Final-combine needs a live two-dimensional color buffer.");
        if (Output is { } output && source.View.NativePointer == output.View.NativePointer)
            throw new ArgumentException("Final-combine input cannot be its output attachment.");
        if (targets is null || targets.Width != source.Width || targets.Height != source.Height)
        {
            var next = renderer.CreateNativeTargets(source.Width, source.Height, Format.R16G16B16A16_Float);
            targets?.Dispose(); targets = next;
            Vector4[] view = new Vector4[13];
            view[12] = new(source.Width, source.Height, 1f / source.Width, 1f / source.Height);
            renderer.UpdateNativeConstantBuffer(pixelView, MemoryMarshal.AsBytes(view.AsSpan()));
        }
        var pass = applyFilmCurve ? film : noFilm;
        pass.Bindings.ApplyTfxResources(pass.PixelEvaluation,
            new Dictionary<TfxResource, ID3D11ShaderResourceView> { [sourceToken] = source.View });
        try
        {
            renderer.ClearNativeTargets(targets, Vector4.Zero, 1);
            renderer.DrawNativeVertices(pass.Pipeline, pass.Bindings, targets, PrimitiveTopology.TriangleStrip, 4);
        }
        finally
        {
            renderer.Context.PSSetShaderResource(0, null!);
            renderer.RestoreMainTarget();
        }
        return targets.Textures[0];
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
