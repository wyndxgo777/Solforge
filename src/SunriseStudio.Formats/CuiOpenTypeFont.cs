using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** SFNT bounds and Unicode mappings are checked before a font reaches the rasterizer. */
public sealed class CuiOpenTypeFont
{
    private readonly ReadOnlyMemory<byte> bytes;
    private readonly int cmap, cmapLength, format, glyphCount, metrics, metricCount;
    public ushort UnitsPerEm { get; }
    public short Ascender { get; }
    public short Descender { get; }
    public ReadOnlyMemory<byte> Bytes => bytes;

    public CuiOpenTypeFont(ReadOnlyMemory<byte> bytes)
    {
        this.bytes = bytes;
        Need(0, 12);
        uint signature = U32(0);
        if (signature is not 0x4F54544F and not 0x00010000)
            throw new NotSupportedException("Font payload is not OpenType or TrueType.");
        int count = U16(4); Need(12, checked(count * 16));
        Dictionary<string, (int Offset, int Length)> tables = [];
        for (int i = 0; i < count; i++)
        {
            int row = 12 + i * 16, offset = checked((int)U32(row + 8)), length = checked((int)U32(row + 12));
            Need(offset, length);
            string name = System.Text.Encoding.ASCII.GetString(bytes.Span.Slice(row, 4));
            if (!tables.TryAdd(name, (offset, length))) throw new InvalidDataException("Duplicate font table.");
        }
        var head = Table("head", 54); var horizontal = Table("hhea", 36); var maximum = Table("maxp", 6);
        UnitsPerEm = U16(head.Offset + 18); Ascender = I16(horizontal.Offset + 4); Descender = I16(horizontal.Offset + 6);
        glyphCount = U16(maximum.Offset + 4); metricCount = U16(horizontal.Offset + 34);
        if (UnitsPerEm is < 16 or > 16384 || Ascender <= Descender || glyphCount == 0
            || metricCount == 0 || metricCount > glyphCount) throw new InvalidDataException("Invalid font metrics.");
        metrics = Table("hmtx", checked(metricCount * 4 + (glyphCount - metricCount) * 2)).Offset;
        if (signature == 0x4F54544F) Table("CFF ", 4);
        else { Table("glyf", 0); Table("loca", 0); }
        var mappings = Table("cmap", 4);
        int mappingCount = U16(mappings.Offset + 2);
        Within(mappings.Offset + 4, mappingCount * 8, mappings);
        int selected = -1, rank = -1;
        for (int i = 0; i < mappingCount; i++)
        {
            int row = mappings.Offset + 4 + i * 8;
            int platform = U16(row), encoding = U16(row + 2);
            if (platform != 0 && (platform != 3 || encoding is not 1 and not 10)) continue;
            int start = checked(mappings.Offset + (int)U32(row + 4)); Within(start, 2, mappings);
            int kind = U16(start), candidateRank = kind == 12 ? 2 : kind == 4 ? 1 : -1;
            if (candidateRank > rank) { rank = candidateRank; selected = start; }
        }
        if (selected < 0) throw new NotSupportedException("Font has no supported Unicode character map.");
        cmap = selected; format = U16(cmap);
        Within(cmap, 16, mappings);
        cmapLength = format == 12 ? checked((int)U32(cmap + 4)) : U16(cmap + 2);
        Within(cmap, cmapLength, mappings);
        if (format == 12)
        {
            int groups = checked((int)U32(cmap + 12)); MapNeed(16, checked(groups * 12));
            uint previous = 0;
            for (int i = 0; i < groups; i++)
            {
                int at = cmap + 16 + i * 12; uint start = U32(at), end = U32(at + 4);
                if (start > end || end > 0x10FFFF || i != 0 && start <= previous
                    || (ulong)U32(at + 8) + end - start >= (uint)glyphCount)
                    throw new InvalidDataException("Invalid font mapping groups.");
                previous = end;
            }
        }
        else
        {
            int segments = U16(cmap + 6) / 2;
            if (segments == 0 || (U16(cmap + 6) & 1) != 0) throw new InvalidDataException("Invalid font mapping segments.");
            MapNeed(14, checked(segments * 8 + 2));
            int previous = -1;
            for (int i = 0; i < segments; i++)
            {
                int end = U16(cmap + 14 + i * 2), start = U16(cmap + 16 + segments * 2 + i * 2);
                if (start > end || end <= previous) throw new InvalidDataException("Unsorted font mapping segments.");
                int range = 16 + segments * 6 + i * 2, distance = U16(cmap + range);
                if (distance != 0)
                {
                    if ((distance & 1) != 0) throw new InvalidDataException("Unaligned font glyph-index array.");
                    MapNeed(range + distance, checked((end - start + 1) * 2));
                }
                previous = end;
            }
        }

        (int Offset, int Length) Table(string name, int minimum)
        {
            if (!tables.TryGetValue(name, out var table) || table.Length < minimum)
                throw new InvalidDataException($"Font table {name} is missing or truncated.");
            return table;
        }
    }

    /**
     * Unsupported or unmapped scalars use glyph zero.
     * @param scalar Unicode scalar value.
     * @return A bounded glyph index.
     */
    public int GlyphIndex(int scalar)
    {
        if ((uint)scalar > 0x10FFFF) return 0;
        uint glyph = 0;
        if (format == 12)
        {
            int low = 0, high = checked((int)U32(cmap + 12)) - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2, at = cmap + 16 + middle * 12;
                uint start = U32(at), end = U32(at + 4);
                if (scalar < start) high = middle - 1;
                else if (scalar > end) low = middle + 1;
                else { glyph = U32(at + 8) + (uint)scalar - start; break; }
            }
        }
        else if (scalar <= ushort.MaxValue)
        {
            int segments = U16(cmap + 6) / 2;
            for (int i = 0; i < segments; i++)
            {
                int end = U16(cmap + 14 + i * 2);
                if (scalar > end) continue;
                int start = U16(cmap + 16 + segments * 2 + i * 2);
                if (scalar < start) break;
                int delta = I16(cmap + 16 + segments * 4 + i * 2);
                int range = cmap + 16 + segments * 6 + i * 2, distance = U16(range);
                if (distance == 0) glyph = (uint)((scalar + delta) & 0xFFFF);
                else
                {
                    int at = checked(range + distance + (scalar - start) * 2);
                    MapNeed(at - cmap, 2); glyph = U16(at);
                    if (glyph != 0) glyph = (uint)(((int)glyph + delta) & 0xFFFF);
                }
                break;
            }
        }
        return glyph < glyphCount ? (int)glyph : 0;
    }

    /** Horizontal advance of the scalar's glyph in pixels at the given em size. */
    public float Advance(int scalar, float emPixels)
    {
        int glyph = GlyphIndex(scalar);
        return U16(metrics + Math.Min(glyph, metricCount - 1) * 4) * emPixels / UnitsPerEm;
    }

    private void MapNeed(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > cmapLength - length)
            throw new InvalidDataException("Font character map leaves its table.");
    }
    private void Need(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
            throw new InvalidDataException("Font table leaves its payload.");
    }
    private static void Within(int offset, int length, (int Offset, int Length) table)
    {
        if (length < 0 || offset < table.Offset || (long)offset + length > (long)table.Offset + table.Length)
            throw new InvalidDataException("Font subtable leaves its parent table.");
    }
    private ushort U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(bytes.Span[at..]);
    private short I16(int at) => BinaryPrimitives.ReadInt16BigEndian(bytes.Span[at..]);
    private uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(bytes.Span[at..]);
}
