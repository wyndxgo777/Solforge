using SunriseStudio.Packages;
using Vortice.DXGI;

namespace SunriseStudio.Viewport;

/** A game texture with its header facts (size, format, array and volume flags). */
public sealed record GameTexture(TextureData Asset)
{
    public uint Tag => Asset.TagHash;
    public int Width => Asset.Width;
    public int Height => Asset.Height;
    public int Depth => Asset.Depth;
    public int ArraySize => Asset.ArraySize;
    public Format Format => (Format)Asset.Format;
    public bool IsSingleImage => !Asset.IsVolume && Asset.ArraySize == 1;

    /** Reads a texture entry from the catalog. */
    public static GameTexture Read(PackageCatalog catalog, PackageEntry entry) =>
        new(new TextureDataReader(catalog).Read(entry.TagHash));

    /** Row pitch and slice size in bytes for a format and size. */
    public static (int RowPitch, int SliceBytes) GetPitch(Format format, int width, int height) =>
        TextureDataReader.GetPitch((int)format, width, height);
}
