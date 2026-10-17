/**
 * File: Retail localized strings (SLocalizedStrings 0x808099EF -> English SStringData 0x808099F1). VERIFIED on
 * 0x80D26002 against alkahest's StringContainer: each string is the UTF-8 concatenation of its parts, and the hashes
 * line up with the combinations.
 */
using System.Text;

namespace Solforge.Retail.Decoding;

/** One localized string: its hash (FNV-1 of the string id) and English text. */
public sealed record RetailString(uint Hash, string Text);

/** Decodes string containers. */
public static class RetailStrings
{
    public const uint ContainerClass = 0x808099EF;
    public const uint DataClass = 0x808099F1;
    private const uint PartClass = 0x808099F7, BytesClass = 0x80800005, CombinationClass = 0x808099F5, HashClass = 0x80800070;

    /** The English strings of a container tag, in container order; problem says why not. */
    public static IReadOnlyList<RetailString> Read(RetailSource source, uint container, out string? problem)
    {
        problem = null;
        if (source.Read(container) is not { } header) { problem = $"{RetailTag.Hex(container)} has no payload."; return []; }
        uint english = RetailBytes.U32(header, 0x18);
        if (source.Read(english) is not { } data) { problem = $"English data tag {RetailTag.Hex(english)} has no payload."; return []; }
        return Decode(header, data, out problem);
    }

    /** Decodes a container's header bytes and its English data bytes. */
    public static IReadOnlyList<RetailString> Decode(ReadOnlySpan<byte> header, ReadOnlySpan<byte> data, out string? problem)
    {
        problem = null;
        if (!RetailBytes.TryArray(header, 0x08, out var hashes) || hashes.Count > 0 && hashes.Class != HashClass)
        { problem = "The container's string hash array does not decode."; return []; }
        if (!RetailBytes.TryArray(data, 0x08, out var parts) || parts.Count > 0 && parts.Class != PartClass
            || !RetailBytes.TryArray(data, 0x28, out var bytes) || bytes.Count > 0 && bytes.Class != BytesClass
            || !RetailBytes.TryArray(data, 0x38, out var combinations) || combinations.Count > 0 && combinations.Class != CombinationClass)
        { problem = "The string data arrays do not decode (expected parts 0x808099F7, bytes 0x80800005, combinations 0x808099F5)."; return []; }
        if (combinations.Count != hashes.Count) problem = $"{hashes.Count} hashes but {combinations.Count} strings; pairing by order.";
        var result = new List<RetailString>(Math.Min(hashes.Count, combinations.Count));
        var text = new StringBuilder();
        for (int i = 0; i < Math.Min(hashes.Count, combinations.Count); i++)
        {
            text.Clear();
            int combination = combinations.Data + i * 16;
            if (!RetailBytes.TryRelative(data, combination, out int first)) { result.Add(new(RetailBytes.U32(header, hashes.Data + i * 4), "")); continue; }
            long count = RetailBytes.I64(data, combination + 8);
            for (int k = 0; k < count && k < 4096; k++)
            {
                int part = first + k * 0x20;
                if (!RetailBytes.Has(data, part, 0x20) || !RetailBytes.TryRelative(data, part + 8, out int at)) break;
                int length = RetailBytes.U16(data, part + 0x14);
                ushort shift = RetailBytes.U16(data, part + 0x18);
                if (!RetailBytes.Has(data, at, length)) break;
                string chunk = Encoding.UTF8.GetString(data.Slice(at, length));
                if (shift != 0)
                {
                    // Charm adds the cipher shift to every character; 0 in every retail part sampled so far.
                    var shifted = new StringBuilder(chunk.Length);
                    foreach (char c in chunk) shifted.Append((char)(c + shift));
                    chunk = shifted.ToString();
                }
                text.Append(chunk);
            }
            result.Add(new(RetailBytes.U32(header, hashes.Data + i * 4), text.ToString()));
        }
        return result;
    }

    /** FNV-1 32-bit (offset 0x811C9DC5, prime 0x01000193) of the UTF-8 bytes; Tiger's name hash. */
    public static uint Fnv1(string text)
    {
        uint hash = 0x811C9DC5;
        foreach (byte value in Encoding.UTF8.GetBytes(text)) { hash *= 0x01000193; hash ^= value; }
        return hash;
    }
}
