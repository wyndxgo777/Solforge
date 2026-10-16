using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** DDS preserves native compressed blocks, mip chains, cube faces and volume slices. */
public static class DdsInterchange
{
    /** Writes a texture to a DDS file. */
    public static void Write(string path, TextureData texture) => File.WriteAllBytes(path, Write(texture));
    /** Reads a texture from a DDS file. */
    public static TextureData Read(string path) => Read(File.ReadAllBytes(path));

    /** The DX10 header keeps DXGI formats explicit instead of guessing legacy bit masks. */
    public static byte[] Write(TextureData texture)
    {
        Validate(texture);
        var bytes = new byte[checked(148 + (int)texture.ByteCount)];
        void U(int p, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(p), value);
        U(0, 0x20534444); U(4, 124);
        bool compressed = IsBlockCompressed(texture.Format);
        uint flags = 0x1007u | (compressed ? 0x80000u : 8u);
        if (texture.MipCount > 1) flags |= 0x20000;
        if (texture.IsVolume) flags |= 0x800000;
        U(8, flags); U(12, (uint)texture.Height); U(16, (uint)texture.Width);
        var top = texture.Subresources.Single(x => x.MipLevel == 0 && x.ArraySlice == 0);
        U(20, (uint)(compressed ? top.SlicePitch * top.Depth : top.RowPitch));
        U(24, texture.IsVolume ? (uint)texture.Depth : 0); U(28, (uint)texture.MipCount);
        U(76, 32); U(80, 4); U(84, 0x30315844);
        U(108, 0x1000u | (texture.MipCount > 1 ? 0x400008u : 0)
            | (texture.ArraySize > 1 || texture.IsVolume ? 8u : 0));
        U(112, texture.IsCube ? 0xFE00u : texture.IsVolume ? 0x200000u : 0);
        U(128, (uint)texture.Format); U(132, texture.IsVolume ? 4u : 3u);
        U(136, texture.IsCube ? 4u : 0); U(140, (uint)(texture.IsCube ? texture.ArraySize / 6 : texture.ArraySize));
        int offset = 148;
        foreach (var subresource in texture.Subresources.OrderBy(x => x.ArraySlice).ThenBy(x => x.MipLevel))
        {
            subresource.Data.CopyTo(bytes, offset);
            offset += subresource.Data.Length;
        }
        return bytes;
    }

    /** Legacy DXT and RGBA files are accepted; unsupported masks are refused. */
    public static TextureData Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 128 || U(bytes, 0) != 0x20534444 || U(bytes, 4) != 124 || U(bytes, 76) != 32)
            throw new InvalidDataException("Invalid DDS header.");
        int width = checked((int)U(bytes, 16)), height = checked((int)U(bytes, 12));
        int depth = Math.Max(1, checked((int)U(bytes, 24)));
        int mips = Math.Max(1, checked((int)U(bytes, 28)));
        uint code = U(bytes, 84), caps = U(bytes, 112);
        int format, array = 1, start = 128;
        TextureKind kind = (caps & 0x200000) != 0 ? TextureKind.Volume
            : (caps & 0x200) != 0 ? TextureKind.Cube : TextureKind.Texture2D;
        if (code == 0x30315844)
        {
            if (bytes.Length < 148) throw new InvalidDataException("Truncated DDS DX10 header.");
            format = checked((int)U(bytes, 128));
            uint dimension = U(bytes, 132);
            if (dimension is not (3 or 4)) throw new NotSupportedException("DDS resource dimension is unsupported.");
            array = checked((int)U(bytes, 140));
            kind = dimension == 4 ? TextureKind.Volume : (U(bytes, 136) & 4) != 0 ? TextureKind.Cube : TextureKind.Texture2D;
            if (kind == TextureKind.Cube) array = checked(array * 6);
            start = 148;
        }
        else if ((U(bytes, 80) & 4) != 0)
        {
            format = code switch
            {
                0x31545844 => 71,
                0x33545844 => 74,
                0x35545844 => 77,
                0x31495441 or 0x55344342 => 80,
                0x32495441 or 0x55354342 => 83,
                _ => throw new NotSupportedException("DDS compression format is unsupported.")
            };
            if (kind == TextureKind.Cube) array = 6;
        }
        else if (U(bytes, 88) == 32 && U(bytes, 96) == 0xFF00 && U(bytes, 104) == 0xFF000000)
        {
            format = (U(bytes, 92), U(bytes, 100)) switch
            {
                (0xFF, 0xFF0000) => 28,
                (0xFF0000, 0xFF) => 87,
                _ => throw new NotSupportedException("DDS channel masks are unsupported.")
            };
            if (kind == TextureKind.Cube) array = 6;
        }
        else throw new NotSupportedException("DDS pixel format is unsupported.");
        CheckDimensions(width, height, depth, array, mips, kind);
        List<TextureSubresourceData> subresources = [];
        for (int slice = 0; slice < array; slice++)
            for (int mip = 0; mip < mips; mip++)
            {
                int w = Math.Max(1, width >> mip), h = Math.Max(1, height >> mip);
                int d = kind == TextureKind.Volume ? Math.Max(1, depth >> mip) : 1;
                var (row, pitch) = TextureDataReader.GetPitch(format, w, h);
                int size = checked(pitch * d);
                if (start > bytes.Length - size) throw new InvalidDataException("Truncated DDS mip data.");
                subresources.Add(new(mip, slice, w, h, d, row, pitch, bytes.Slice(start, size).ToArray()));
                start = checked(start + size);
            }
        if (start != bytes.Length) throw new InvalidDataException("DDS contains unaccounted trailing data.");
        return new(0, format, width, height, depth, array, mips, 0, kind, "DDS", subresources);
    }

    /** Each subresource must match its declared mip dimensions and byte layout. */
    public static void Validate(TextureData texture)
    {
        CheckDimensions(texture.Width, texture.Height, texture.Depth, texture.ArraySize, texture.MipCount, texture.Kind);
        if (texture.Subresources.Count != texture.MipCount * texture.ArraySize
            || texture.Subresources.Select(x => (x.MipLevel, x.ArraySlice)).Distinct().Count() != texture.Subresources.Count)
            throw new InvalidDataException("Texture subresources are missing or repeated.");
        foreach (var sub in texture.Subresources)
        {
            if (sub.MipLevel < 0 || sub.MipLevel >= texture.MipCount || sub.ArraySlice < 0 || sub.ArraySlice >= texture.ArraySize)
                throw new InvalidDataException("Texture subresource index is invalid.");
            int w = Math.Max(1, texture.Width >> sub.MipLevel), h = Math.Max(1, texture.Height >> sub.MipLevel);
            int d = texture.IsVolume ? Math.Max(1, texture.Depth >> sub.MipLevel) : 1;
            var (row, slice) = TextureDataReader.GetPitch(texture.Format, w, h);
            if (sub.Width != w || sub.Height != h || sub.Depth != d || sub.RowPitch != row
                || sub.SlicePitch != slice || sub.Data.Length != checked(slice * d))
                throw new InvalidDataException("Texture subresource layout is inconsistent.");
        }
    }

    /** D3D11 texture limits bound allocations before mip parsing. */
    private static void CheckDimensions(int width, int height, int depth, int array, int mips, TextureKind kind)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || depth is < 1 or > 2048
            || array is < 1 or > 2048 || mips is < 1 or > 15)
            throw new InvalidDataException("DDS dimensions are outside supported limits.");
        if (kind == TextureKind.Volume && (array != 1 || width > 2048 || height > 2048)
            || kind != TextureKind.Volume && depth != 1
            || kind == TextureKind.Cube && (array % 6 != 0 || width != height))
            throw new InvalidDataException("DDS dimension and resource kind disagree.");
        int maximum = Math.Max(width, Math.Max(height, depth));
        if (mips > 1 + (int)Math.Log2(maximum)) throw new InvalidDataException("DDS has too many mip levels.");
    }

    private static uint U(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static bool IsBlockCompressed(int format) => format is >= 70 and <= 84 or >= 94 and <= 99;
}
