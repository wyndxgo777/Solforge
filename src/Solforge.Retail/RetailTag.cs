/**
 * File: Tag hash arithmetic and bounds-checked readers for retail tag bytes (typed arrays with the retail marker,
 * relative pointers, WideHash references).
 */
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Solforge.Retail;

/** Tag hash helpers: tag = 0x80800000 + (package << 13) + index. */
public static class RetailTag
{
    /** Typed-array header marker in build 245048 (86657 uses 0x80809FBD). */
    public const uint ArrayMarker = 0x80809FB8;
    public const uint Base = 0x80800000;

    public static ushort Package(uint tag) => (ushort)((tag - Base) >> 13);
    public static int Index(uint tag) => (int)((tag - Base) & 0x1FFF);
    public static uint Make(ushort package, int index) => Base + ((uint)package << 13) + (uint)index;
    /** Whether a u32 looks like a tag reference (0x80800000..0xBFFFFFFF). */
    public static bool Plausible(uint value) => value >= Base && value < 0xC0000000;
    public static string Hex(uint value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);

    /** Parses "0x80d26002", "80D26002" or "0293:2" (package:index); null when it is none of them. */
    public static uint? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        int colon = text.IndexOf(':');
        if (colon > 0)
            return ushort.TryParse(text[..colon], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort package)
                && int.TryParse(text[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index is >= 0 and < 8192
                ? Make(package, index) : null;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return text.Length is > 0 and <= 8 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) ? value : null;
    }
}

/** A typed array: element count, element class and the absolute offset of the first element. */
public readonly record struct RetailArray(int Count, uint Class, int Data)
{
    public static readonly RetailArray Empty = new(0, 0, -1);
}

/** A 16-byte WideHash: a 32-bit tag when Is32 is set, otherwise a 64-bit hash resolved through the lookup cache. */
public readonly record struct WideHash(uint Hash32, bool Is32, ulong Hash64)
{
    public bool IsNone => Is32 ? Hash32 is 0 or 0xFFFFFFFF : Hash64 is 0 or ulong.MaxValue;
    public override string ToString() => Is32 ? RetailTag.Hex(Hash32) : "h64:0x" + Hash64.ToString("X16", CultureInfo.InvariantCulture);
}

/** Bounds-checked little-endian reads over one tag's bytes. Every Try method returns false instead of throwing. */
public static class RetailBytes
{
    public static bool Has(ReadOnlySpan<byte> bytes, int offset, int width) => offset >= 0 && width >= 0 && (long)offset + width <= bytes.Length;
    public static uint U32(ReadOnlySpan<byte> bytes, int offset) => Has(bytes, offset, 4) ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) : 0;
    public static ushort U16(ReadOnlySpan<byte> bytes, int offset) => Has(bytes, offset, 2) ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]) : (ushort)0;
    public static short I16(ReadOnlySpan<byte> bytes, int offset) => Has(bytes, offset, 2) ? BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]) : (short)0;
    public static ulong U64(ReadOnlySpan<byte> bytes, int offset) => Has(bytes, offset, 8) ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]) : 0;
    public static long I64(ReadOnlySpan<byte> bytes, int offset) => Has(bytes, offset, 8) ? BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]) : 0;
    public static float F32(ReadOnlySpan<byte> bytes, int offset) => Has(bytes, offset, 4) ? BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]) : 0f;
    public static byte U8(ReadOnlySpan<byte> bytes, int offset) => Has(bytes, offset, 1) ? bytes[offset] : (byte)0;

    /** Reads the typed array whose 16-byte descriptor is at field (u64 count, i64 offset from field+8 to the header). */
    public static bool TryArray(ReadOnlySpan<byte> bytes, int field, out RetailArray array, uint marker = RetailTag.ArrayMarker)
    {
        array = RetailArray.Empty;
        if (!Has(bytes, field, 16)) return false;
        ulong count = U64(bytes, field);
        if (count == 0) return true;
        if (count > 4_000_000) return false;
        long header = field + 8L + I64(bytes, field + 8);
        if (header < 4 || header + 16 > bytes.Length) return false;
        int at = (int)header;
        if (U32(bytes, at - 4) != marker || U64(bytes, at) != count) return false;
        array = new RetailArray((int)count, U32(bytes, at + 8), at + 16);
        return true;
    }

    /** The typed array at field, or an empty one when it does not decode. */
    public static RetailArray Array(ReadOnlySpan<byte> bytes, int field) => TryArray(bytes, field, out var array) ? array : RetailArray.Empty;

    /** Resolves a relative pointer (i64 from the pointer word); false when zero or outside the tag. */
    public static bool TryRelative(ReadOnlySpan<byte> bytes, int field, out int target)
    {
        target = -1;
        if (!Has(bytes, field, 8)) return false;
        long delta = I64(bytes, field);
        if (delta == 0) return false;
        long result = field + delta;
        if (result < 0 || result >= bytes.Length) return false;
        target = (int)result;
        return true;
    }

    /** The WideHash at offset (u32 hash32, u32 is-32-bit flag, u64 hash64). */
    public static WideHash Wide(ReadOnlySpan<byte> bytes, int offset) =>
        new(U32(bytes, offset), U32(bytes, offset + 4) != 0, U64(bytes, offset + 8));

    /** A NUL-terminated ASCII string at offset, or null. */
    public static string? CString(ReadOnlySpan<byte> bytes, int offset, int max = 512)
    {
        if (offset < 0 || offset >= bytes.Length) return null;
        int end = Math.Min(bytes.Length, offset + max);
        for (int i = offset; i < end; i++)
        {
            if (bytes[i] == 0) return Encoding.ASCII.GetString(bytes[offset..i]);
            if (bytes[i] is > 0x7E or < 0x20) return null;
        }
        return null;
    }

    /** Annotated hex dump (offset, 16 bytes, ASCII). */
    public static string HexDump(ReadOnlySpan<byte> bytes, int offset, int length)
    {
        var text = new StringBuilder();
        int end = Math.Min(bytes.Length, offset + Math.Max(0, length));
        for (int row = Math.Max(0, offset); row < end; row += 16)
        {
            text.Append(row.ToString("X5", CultureInfo.InvariantCulture)).Append("  ");
            for (int i = 0; i < 16; i++)
                text.Append(row + i < end ? bytes[row + i].ToString("x2", CultureInfo.InvariantCulture) + " " : "   ");
            text.Append(' ');
            for (int i = 0; i < 16 && row + i < end; i++) text.Append(bytes[row + i] is >= 0x20 and < 0x7F ? (char)bytes[row + i] : '.');
            text.Append('\n');
        }
        return text.ToString();
    }
}
