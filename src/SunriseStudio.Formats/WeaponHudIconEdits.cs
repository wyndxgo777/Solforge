using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** HUD silhouettes use named icon-library rows and a weapon variant's glyph hash. */
public static class WeaponHudIconEdits
{
    public const uint LibraryTag = 0x80EFC07B, LibraryClass = 0x80804A55;
    public const uint ConfigTemplate = 0x8152C7C5, BitmapTemplate = 0x80BC7119, TextureTemplate = 0x80BC7117;
    public const uint TemplateGlyph = 0xFC0A6758;
    public const int Width = 137, Height = 76;
    private const int VariantIndex = 20, LibraryStride = 112, VariantStride = 448, GlyphOffset = 224;

    /** Name hashes of the glyphs in the icon library. */
    public static IReadOnlyList<uint> GlyphHashes(ReadOnlyMemory<byte> library)
    {
        var reader = new TagReader(library);
        var rows = reader.Array(8, 0x80804A59, LibraryStride);
        return Enumerable.Range(0, rows.Count).Select(index => reader.U32(rows.Data + index * LibraryStride)).ToArray();
    }

    /** New named rows are unsigned-sorted without changing any existing icon record. */
    public static byte[] AddGlyph(ReadOnlyMemory<byte> library, uint nameHash, uint bitmap)
    {
        var reader = new TagReader(library);
        var rows = reader.Array(8, 0x80804A59, LibraryStride);
        var hashes = GlyphHashes(library);
        if (nameHash is 0 or uint.MaxValue or InvestmentItemClone.EmptyStringHash
            || bitmap is 0 or uint.MaxValue || hashes.Contains(nameHash)
            || !hashes.SequenceEqual(hashes.Order()) || hashes.Distinct().Count() != hashes.Count)
            throw new InvalidDataException("HUD icon names must be unique and unsigned-sorted.");
        int source = Enumerable.Range(0, rows.Count).Single(index => hashes[index] == TemplateGlyph);
        byte[] row = library.Span.Slice(rows.Data + source * LibraryStride, LibraryStride).ToArray();
        Put32(row, 0, nameHash); Put32(row, 4, bitmap);
        byte[] output = InvestmentItemClone.AppendRow(library, 8, LibraryStride, row);
        var grown = new TagReader(output).Array(8, 0x80804A59, LibraryStride);
        byte[][] ordered = Enumerable.Range(0, grown.Count)
            .Select(index => output.AsSpan(grown.Data + index * LibraryStride, LibraryStride).ToArray())
            .OrderBy(bytes => U32(bytes, 0)).ToArray();
        for (int index = 0; index < ordered.Length; index++) ordered[index].CopyTo(output, grown.Data + index * LibraryStride);
        return output;
    }

    /** The bitmap a named library row selects, or null when no row has the name. */
    public static uint? GlyphBitmap(ReadOnlyMemory<byte> library, uint nameHash)
    {
        var reader = new TagReader(library);
        var rows = reader.Array(8, 0x80804A59, LibraryStride);
        for (int index = 0; index < rows.Count; index++)
            if (reader.U32(rows.Data + index * LibraryStride) == nameHash) return reader.U32(rows.Data + index * LibraryStride + 4);
        return null;
    }

    /** Reads the HUD glyph name hash of the selected weapon variant. */
    public static uint ReadGlyph(ReadOnlyMemory<byte> config) => new TagReader(config).U32(VariantGlyphOffset(config));

    /** Only the selected weapon variant receives the new HUD glyph name. */
    public static byte[] SetGlyph(ReadOnlyMemory<byte> config, uint nameHash)
    {
        if (nameHash is 0 or uint.MaxValue or InvestmentItemClone.EmptyStringHash)
            throw new InvalidDataException("A HUD glyph needs a nonempty name hash.");
        byte[] result = config.ToArray(); Put32(result, VariantGlyphOffset(config), nameHash);
        return result;
    }

    /** Reciprocal owners must cover every embedded reference to the cloned config. */
    public static byte[] RetagConfig(ReadOnlyMemory<byte> config, uint source, uint target)
    {
        var (bytes, pairs) = WeaponConfigEdits.RetagConfig(config, source, target);
        if (pairs != 24) throw new InvalidDataException("The HUD weapon config owner pairs changed.");
        return bytes;
    }

    private static int VariantGlyphOffset(ReadOnlyMemory<byte> config)
    {
        var reader = new TagReader(config);
        int resource = reader.Relative(24);
        if (resource < 4 || reader.U32(resource - 4) != 0x80803AC9)
            throw new InvalidDataException("The HUD weapon resource changed class.");
        var rows = reader.Array(resource + 0x240, 0x80803ACF, VariantStride);
        if (rows.Count != 22) throw new InvalidDataException("The HUD weapon variant table changed.");
        return rows.Data + VariantIndex * VariantStride + GlyphOffset;
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
