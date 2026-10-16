using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** Reimport keeps native texture headers intact and replaces their payload entries. */
public static class TextureEdits
{
    /** Builds the tag changes (by tag hash) that replace the texture of a header with new texture data. */
    public static IReadOnlyDictionary<uint, byte[]> Apply(PackageCatalog catalog, uint headerTag,
        TextureData replacement)
    {
        var original = new TextureDataReader(catalog).Read(headerTag);
        if (!catalog.TryGetEntry(headerTag, out var entry)) throw new InvalidDataException("Texture entry is missing.");
        return Apply(original, catalog.Read(headerTag), entry.Reference, replacement);
    }

    /** Native package ordering is mip-major even though DDS ordering is array-major. */
    public static IReadOnlyDictionary<uint, byte[]> Apply(TextureData original, ReadOnlySpan<byte> header,
        uint payloadTag, TextureData replacement)
    {
        DdsInterchange.Validate(original);
        DdsInterchange.Validate(replacement);
        if (header.Length != 40 || original.Width != replacement.Width || original.Height != replacement.Height
            || original.Depth != replacement.Depth || original.Format != replacement.Format
            || original.MipCount != replacement.MipCount || original.ArraySize != replacement.ArraySize
            || original.Kind != replacement.Kind)
            throw new InvalidDataException("Replacement must keep texture dimensions, format, mip count and kind.");
        if (payloadTag is 0 or uint.MaxValue || original.LargeMipCount < 0 || original.LargeMipCount > original.MipCount
            || BinaryPrimitives.ReadUInt16LittleEndian(header[0x0C..]) != 0xCAFE
            || BinaryPrimitives.ReadUInt32LittleEndian(header) != original.ByteCount)
            throw new InvalidDataException("Original texture header and payload layout disagree.");
        using var tail = new MemoryStream();
        using var large = new MemoryStream();
        foreach (var sub in replacement.Subresources.OrderBy(x => x.MipLevel).ThenBy(x => x.ArraySlice))
            (sub.MipLevel < original.LargeMipCount ? large : tail).Write(sub.Data);
        Dictionary<uint, byte[]> result = new() { [payloadTag] = tail.ToArray() };
        if (original.LargeMipCount != 0)
        {
            uint largeTag = BinaryPrimitives.ReadUInt32LittleEndian(header[0x24..]);
            if (largeTag is 0 or uint.MaxValue) throw new InvalidDataException("Missing streamed mip payload.");
            result.Add(largeTag, large.ToArray());
        }
        return result;
    }

    /** Added textures preserve unknown header fields from an existing 2D RGBA8 texture. */
    public static (byte[] Header, byte[] Payload) CreateRgbaTexture(RgbaImage image,
        ReadOnlySpan<byte> templateHeader)
    {
        image.Validate();
        if (templateHeader.Length != 40 || BinaryPrimitives.ReadUInt16LittleEndian(templateHeader[0x0C..]) != 0xCAFE
            || BinaryPrimitives.ReadUInt32LittleEndian(templateHeader[4..]) is not (28 or 29))
            throw new InvalidDataException("A native RGBA8 texture header is required.");
        var header = templateHeader.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)image.Pixels.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0C), 0xCAFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0E), checked((ushort)image.Width));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x10), checked((ushort)image.Height));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x14), 1);
        header[0x17] = 1;
        header[0x18] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x24), uint.MaxValue);
        return (header, (byte[])image.Pixels.Clone());
    }
}
