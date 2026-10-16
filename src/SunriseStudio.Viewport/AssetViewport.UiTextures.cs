using SunriseStudio.Packages;
using SunriseStudio.Rendering;
using Vortice.DXGI;

namespace SunriseStudio.Viewport;

/** UI texture half of AssetViewport: preview textures for the editor panels. */
public sealed partial class AssetViewport
{
    /** The caller owns the result and must keep it alive until ImGui finishes the frame. */
    public GpuTexture CreatePreviewTexture(TextureData data) => CreatePreviewTexture(renderer, data);

    /** UI previews sample encoded color bytes directly into the editor's display surface. */
    public static GpuTexture CreatePreviewTexture(Dx11Renderer renderer, TextureData data)
    {
        if (data.Kind != TextureKind.Texture2D || data.Depth != 1)
            throw new NotSupportedException("The UI preview requires a 2D texture.");
        var first = data.Subresources.First(resource => resource.MipLevel == 0 && resource.ArraySlice == 0);
        var format = (Format)data.Format;
        format = format switch
        {
            Format.R8G8B8A8_UNorm_SRgb => Format.R8G8B8A8_UNorm,
            Format.B8G8R8A8_UNorm_SRgb => Format.B8G8R8A8_UNorm,
            Format.BC1_UNorm_SRgb => Format.BC1_UNorm,
            Format.BC2_UNorm_SRgb => Format.BC2_UNorm,
            Format.BC3_UNorm_SRgb => Format.BC3_UNorm,
            Format.BC7_UNorm_SRgb => Format.BC7_UNorm,
            _ => format
        };
        return renderer.CreateTexture(first.Width, first.Height, format, first.Data.AsSpan(0, first.SlicePitch), first.RowPitch);
    }
}
