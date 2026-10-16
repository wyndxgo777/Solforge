using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/** The G-buffer textures and depth view that later passes read. This file also owns the G-buffer targets. */
public sealed record NativeGBufferInputs(GpuTexture Albedo,
    GpuTexture Normal, GpuTexture Third,
    ID3D11ShaderResourceView Depth);

/** Build-86657 G-buffer read surfaces are separate allocations from their write surfaces. */
public sealed class NativeGBufferTargets : IDisposable
{
    private static readonly NativeColorTargetFormat[] Formats =
    [
        new(Format.R8G8B8A8_Typeless, Format.R8G8B8A8_UNorm_SRgb, Format.R8G8B8A8_UNorm_SRgb),
        new(Format.R10G10B10A2_Typeless, Format.R10G10B10A2_UNorm, Format.R10G10B10A2_UNorm),
        new(Format.R8G8B8A8_Typeless, Format.R8G8B8A8_UNorm, Format.R8G8B8A8_UNorm)
    ];
    private readonly Dx11Renderer renderer;
    private readonly NativeRenderTargets readTargets;
    public NativeRenderTargets WriteTargets { get; }
    public NativeGBufferInputs? Inputs { get; private set; }
    /** Stencil plane of the read depth, as target 26 (stencil_alias_read) views target 25. */
    public ID3D11ShaderResourceView StencilRead { get; }

    public NativeGBufferTargets(Dx11Renderer renderer, int width, int height)
    {
        this.renderer = renderer;
        WriteTargets = renderer.CreateNativeTargets(width, height, Formats,
            Format.D32_Float_S8X24_UInt, true);
        try
        {
            readTargets = renderer.CreateNativeTargets(width, height, Formats,
                Format.D32_Float_S8X24_UInt, true);
            StencilRead = renderer.Device.CreateShaderResourceView(readTargets.Depth,
                new ShaderResourceViewDescription(readTargets.Depth, Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                    Format.X32_Typeless_G8X24_UInt));
        }
        catch { readTargets?.Dispose(); WriteTargets.Dispose(); throw; }
    }

    /** Clear values are explicit until each native pass's clear producer is supplied. */
    public void Clear(Vector4 color, float depth, byte stencil = 0)
    {
        Inputs = null;
        renderer.ClearNativeTargets(WriteTargets, color, depth, stencil);
    }

    /** Copies depth and stencil after stage 0 and before the decal stages, as the native pass does. */
    public void CaptureDepthStencil()
    {
        renderer.RestoreMainTarget();
        renderer.Context.CopyResource(readTargets.Depth, WriteTargets.Depth);
    }

    /** Normals copy on the read surface; the decal stages read it there while they write the target. */
    public GpuTexture NormalRead => readTargets.Textures[1];

    /** Copies the normals before the decal stages; Resolve overwrites the copy after them. */
    public void CaptureNormals()
    {
        renderer.RestoreMainTarget();
        renderer.Context.CopyResource(readTargets.Textures[1].Resource, WriteTargets.Textures[1].Resource);
    }

    /** This route copies depth and normals; the conditional modification pass remains separate. */
    public NativeGBufferInputs Resolve()
    {
        renderer.RestoreMainTarget();
        renderer.Context.CopyResource(readTargets.Depth, WriteTargets.Depth);
        renderer.Context.CopyResource(readTargets.Textures[1].Resource, WriteTargets.Textures[1].Resource);
        Inputs = new(WriteTargets.Textures[0], readTargets.Textures[1], WriteTargets.Textures[2],
            readTargets.DepthShaderView!);
        return Inputs;
    }

    /**
     * Reads the stencil plane of the last capture, one byte per pixel in row order.
     * @return Width times height values.
     */
    public unsafe byte[] ReadStencilCopy()
    {
        var description = readTargets.Depth.Description;
        description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read; description.MiscFlags = ResourceOptionFlags.None;
        using var staging = renderer.Device.CreateTexture2D(description);
        renderer.Context.CopyResource(staging, readTargets.Depth);
        var mapped = renderer.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            // R32G8X24 texels are eight bytes; the stencil is byte 4.
            var values = new byte[description.Width * description.Height];
            for (int y = 0; y < description.Height; y++)
            {
                byte* row = (byte*)mapped.DataPointer + y * mapped.RowPitch;
                for (int x = 0; x < description.Width; x++) values[y * description.Width + x] = row[x * 8 + 4];
            }
            return values;
        }
        finally { renderer.Context.Unmap(staging, 0); }
    }

    /** Releases the read, write and stencil views. */
    public void Dispose() { StencilRead.Dispose(); readTargets.Dispose(); WriteTargets.Dispose(); }
}
