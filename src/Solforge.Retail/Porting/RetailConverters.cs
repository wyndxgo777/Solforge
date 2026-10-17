/**
 * File: Byte-level converters from retail (245048) to Sol (86657) tag data: typed-array marker and element-class
 * rewrite, reference relinking, and localized strings to Sol string-bank rows. They only change what they can prove
 * (array headers they decode, references the caller maps); everything else is reported, never guessed.
 */
using System.Buffers.Binary;
using Solforge.Retail.Decoding;
using SunriseStudio.Packages;
using SunriseStudio.Packages.TagSchemas;

namespace Solforge.Retail.Porting;

/** What a conversion changed and what it could not. */
public sealed record RetailConversion(byte[] Bytes, int ArraysRewritten, int ClassesMapped, IReadOnlyList<string> UnmappedClasses,
    int ReferencesRelinked, IReadOnlyList<uint> UnmappedReferences, IReadOnlyList<string> Notes);

/** Retail -> Sol converters. */
public static class RetailConverters
{
    /**
     * Finds every typed-array header reachable from a descriptor in bytes (u64 count, i64 offset to a header that repeats
     * the count after the retail marker). Scanning every 8-aligned word finds nested arrays too.
     */
    public static IReadOnlyList<(int Descriptor, int Header, int Count, uint Class)> FindArrays(ReadOnlySpan<byte> bytes)
    {
        var result = new List<(int, int, int, uint)>();
        var headers = new HashSet<int>();
        for (int at = 0; at + 16 <= bytes.Length; at += 8)
        {
            ulong count = RetailBytes.U64(bytes, at);
            if (count is 0 or > 1_000_000) continue;
            long header = at + 8L + RetailBytes.I64(bytes, at + 8);
            if (header < 4 || header + 16 > bytes.Length || (header & 7) != 0) continue;
            int h = (int)header;
            if (RetailBytes.U32(bytes, h - 4) != RetailTag.ArrayMarker || RetailBytes.U64(bytes, h) != count) continue;
            if (headers.Add(h)) result.Add((at, h, (int)count, RetailBytes.U32(bytes, h + 8)));
        }
        return result;
    }

    /**
     * Rewrites array markers 0x80809FB8 -> 0x80809FBD and, where the class map names exactly one 86657 class, each
     * array's element class; relink maps retail tags to Sol tags (u32 words equal to a key are replaced). The struct
     * layout itself is not changed: only use this for classes whose retail and 86657 sizes and fields agree.
     */
    public static RetailConversion Convert(ReadOnlySpan<byte> retail, RetailClassMap classMap, IReadOnlyDictionary<uint, uint>? relink = null,
        IEnumerable<uint>? references = null)
    {
        byte[] bytes = retail.ToArray();
        var notes = new List<string>();
        var unmappedClasses = new SortedSet<string>(StringComparer.Ordinal);
        int arrays = 0, classes = 0;
        foreach (var (_, header, _, elementClass) in FindArrays(retail))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(header - 4), TagSchemaReader.ArrayMarker);
            arrays++;
            if (elementClass < 0x80800000 || elementClass > 0x8081FFFF) continue;
            if (classMap.SolClassOf(elementClass) is { } sol)
            {
                if (sol != elementClass) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(header + 8), sol); classes++; }
            }
            else if (elementClass > 0x80800100) unmappedClasses.Add(RetailTag.Hex(elementClass)); // low ids are primitive element types
        }
        int relinked = 0;
        var unmapped = new SortedSet<uint>();
        if (references is not null)
            foreach (uint tag in references)
                if (relink?.ContainsKey(tag) != true) unmapped.Add(tag);
        if (relink is { Count: > 0 })
            for (int at = 0; at + 4 <= bytes.Length; at += 4)
                if (relink.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)), out uint target))
                { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), target); relinked++; }
        if (unmappedClasses.Count > 0) notes.Add("Arrays whose element class has no single 86657 class keep the retail class; the game will reject them until mapped.");
        notes.Add("Field layouts are not converted; a class whose 86657 size or fields differ needs its own converter.");
        return new RetailConversion(bytes, arrays, classes, unmappedClasses.ToList(), relinked, unmapped.ToList(), notes);
    }

    /** Retail strings as Sol string-bank rows (hashes kept so ported data that names them still resolves). */
    public static IReadOnlyList<StringBankText> ToBankTexts(IEnumerable<RetailString> strings, out IReadOnlyList<string> notes)
    {
        var rows = new List<StringBankText>();
        var problems = new List<string>();
        var seen = new HashSet<uint>();
        foreach (var row in strings)
        {
            if (!seen.Add(row.Hash)) { problems.Add($"Duplicate hash 0x{row.Hash:X8} dropped."); continue; }
            if (System.Text.Encoding.UTF8.GetByteCount(row.Text) > StringBankWriter.MaximumTextBytes) { problems.Add($"0x{row.Hash:X8} is too long for a bank part."); continue; }
            rows.Add(new StringBankText(row.Hash, row.Text));
        }
        if (rows.Count > StringBankWriter.MaximumStrings) { problems.Add($"Only the first {StringBankWriter.MaximumStrings} strings fit one bank."); rows = rows.Take(StringBankWriter.MaximumStrings).ToList(); }
        notes = problems;
        return rows;
    }

    /**
     * Packs retail strings into a Sol (0x80809A88/0x80809A8A) bank using an installed 86657 bank as the template
     * (StringBankWriter keeps every template byte it does not decode). Returns the container and data payloads.
     */
    public static (byte[] Container, byte[] Data) ToBank(StringBankTemplate template, uint dataTag, IReadOnlyList<StringBankText> rows) =>
        StringBankWriter.Write(template, dataTag, rows);
}
