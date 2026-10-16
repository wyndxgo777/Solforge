using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SunriseStudio.Rendering;

/** One mip and array slice of data for a texture upload, with row and slice pitch. */
public sealed record TextureSubresourceUpload(int MipLevel, int ArraySlice,
    byte[] Data, int RowPitch, int SlicePitch);

/**
 * Texture-resource part of Dx11Renderer: creates 2D, array, cube and volume textures from decoded
 * subresources.
 */
public sealed unsafe partial class Dx11Renderer
{
    /** Each subresource carries explicit mip and array indices; no package ordering is assumed. */
    public GpuTexture CreateTextureResource(int width, int height, int depth, int arraySize,
        int mipLevels, Format format, bool isCube, bool isVolume,
        IReadOnlyList<TextureSubresourceUpload> subresources)
    {
        int limit = isVolume ? 2048 : 16384;
        if (width is < 1 || height is < 1 || width > limit || height > limit
            || depth is < 1 or > 2048 || arraySize is < 1 or > 2048
            || mipLevels < 1 || mipLevels > 1 + (int)Math.Log2(Math.Max(width, Math.Max(height, depth)))
            || isVolume && (isCube || arraySize != 1) || !isVolume && depth != 1
            || isCube && (width != height || arraySize % 6 != 0))
            throw new ArgumentException("Texture dimensions, mip count, or array shape are invalid.");
        bool compressed = format is >= Format.BC1_Typeless and <= Format.BC5_SNorm
            or >= Format.BC6H_Typeless and <= Format.BC7_UNorm_SRgb;
        // BC volumes compress each depth slice in 4x4 blocks; the device check below refuses unsupported formats.
        var requiredSupport = isVolume ? FormatSupport.Texture3D : FormatSupport.Texture2D;
        if ((Device.CheckFormatSupport(format) & requiredSupport) == 0)
            throw new NotSupportedException($"Device format {format} lacks {requiredSupport} support.");
        int count = checked(mipLevels * arraySize);
        if (subresources.Count != count) throw new ArgumentException("Texture subresource count is incomplete.");
        var ordered = new TextureSubresourceUpload?[count];
        foreach (var resource in subresources)
        {
            if ((uint)resource.MipLevel >= mipLevels || (uint)resource.ArraySlice >= arraySize)
                throw new ArgumentException("Texture subresource index is out of range.");
            int index = resource.MipLevel + resource.ArraySlice * mipLevels;
            if (ordered[index] is not null) throw new ArgumentException("Texture subresource index is duplicated.");
            int mipWidth = Math.Max(1, width >> resource.MipLevel);
            int mipHeight = Math.Max(1, height >> resource.MipLevel);
            int mipDepth = isVolume ? Math.Max(1, depth >> resource.MipLevel) : 1;
            ValidateTextureData(mipWidth, mipHeight, format, resource.Data.Length, resource.RowPitch);
            int rows = compressed ? Math.Max(1, (mipHeight + 3) / 4) : mipHeight;
            int minimumSlice = checked(resource.RowPitch * rows);
            if (resource.SlicePitch < minimumSlice
                || resource.Data.Length < checked((mipDepth - 1) * resource.SlicePitch + minimumSlice))
                throw new ArgumentException("Texture depth slices leave their supplied bytes.");
            ordered[index] = resource;
        }
        var pins = new GCHandle[count];
        var native = new SubresourceData[count];
        ID3D11Resource? texture = null;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var resource = ordered[i]!;
                pins[i] = GCHandle.Alloc(resource.Data, GCHandleType.Pinned);
                native[i] = new(pins[i].AddrOfPinnedObject(), (uint)resource.RowPitch, (uint)resource.SlicePitch);
            }
            ShaderResourceViewDescription viewDescription;
            if (isVolume)
            {
                texture = Device.CreateTexture3D(new Texture3DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    Depth = (uint)depth,
                    MipLevels = (uint)mipLevels,
                    Format = format,
                    Usage = ResourceUsage.Immutable,
                    BindFlags = BindFlags.ShaderResource
                }, native);
                viewDescription = new(ShaderResourceViewDimension.Texture3D, format, 0, (uint)mipLevels);
            }
            else
            {
                texture = Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    ArraySize = (uint)arraySize,
                    MipLevels = (uint)mipLevels,
                    Format = format,
                    Usage = ResourceUsage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                    SampleDescription = SampleDescription.Default,
                    MiscFlags = isCube ? ResourceOptionFlags.TextureCube : ResourceOptionFlags.None
                }, native);
                var dimension = isCube ? arraySize == 6 ? ShaderResourceViewDimension.TextureCube
                    : ShaderResourceViewDimension.TextureCubeArray : arraySize == 1
                    ? ShaderResourceViewDimension.Texture2D : ShaderResourceViewDimension.Texture2DArray;
                viewDescription = new(dimension, format, 0, (uint)mipLevels, 0,
                    (uint)(isCube ? arraySize / 6 : arraySize));
            }
            return new(texture, Device.CreateShaderResourceView(texture, viewDescription),
                width, height, depth, arraySize, mipLevels);
        }
        catch { texture?.Dispose(); throw; }
        finally { foreach (var pin in pins) if (pin.IsAllocated) pin.Free(); }
    }
}
