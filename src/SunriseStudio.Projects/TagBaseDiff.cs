using System.Buffers.Binary;
using System.Globalization;

namespace SunriseStudio.Projects;

/**
 * One named field of a tag, from a decoder (an asset inspector's editable property) or a tag schema. Kind is the
 * scalar encoding: u8, i16, u16, i32, u32, i64, u64, f32 or hash (u32 shown as hex); anything else shows as bytes.
 */
public sealed record TagFieldSpan(string Name, int Offset, int Size, string Kind, string Source);

/** A named field whose base and authored values differ. */
public sealed record TagFieldChange(TagFieldSpan Field, string? Base, string? Authored);

/** A run of differing bytes no named field covers; a missing side (size change) has a null hex string. */
public sealed record TagByteRange(int Offset, int Length, string? Base, string? Authored);

/** The field-level view of a ProjectTagDiff: named field changes first, then byte ranges for the rest. */
public sealed record TagBaseComparison(ProjectTagDiff Diff, IReadOnlyList<TagFieldChange> Fields, IReadOnlyList<TagByteRange> Ranges,
    int RangeCount, IReadOnlyList<TagFieldSpan> Known);

/**
 * T1.5 compare_with_base / revert_to_base. Builds on ProjectTagDiff (the "Compare with base..." dialog's diff): where a
 * decoder or schema names fields, changed bytes are reported per field with both values; every other changed byte is
 * reported as a merged byte range. Reverting one field copies the base bytes of that field over the authored bytes.
 */
public static class TagBaseDiff
{
    /** Byte ranges listed before the rest are only counted. */
    public const int MaximumRanges = 256;
    /** Hex bytes shown per side of one range. */
    public const int MaximumRangeBytes = 64;

    /** Splits a diff into changed fields and uncovered byte ranges; fieldFilter keeps one field (name or offset range). */
    public static TagBaseComparison Compare(ProjectTagDiff diff, IReadOnlyList<TagFieldSpan> fields, string? fieldFilter = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(fields);
        byte[] left = diff.Base, right = diff.Authored;
        var changed = new List<TagFieldChange>();
        var covered = new bool[Math.Max(left.Length, right.Length)];
        TagFieldSpan? only = fieldFilter is { Length: > 0 } ? Find(fields, fieldFilter) : null;
        foreach (var field in fields.Where(field => field.Offset >= 0 && field.Size > 0).DistinctBy(field => (field.Offset, field.Size)))
        {
            if (!Differs(left, right, field.Offset, field.Size)) continue;
            for (int i = field.Offset; i < Math.Min(covered.Length, field.Offset + field.Size); i++) covered[i] = true;
            if (only is null || only == field) changed.Add(new(field, Format(left, field), Format(right, field)));
        }
        var ranges = new List<TagByteRange>();
        int count = 0;
        foreach (var (offset, length) in ChangedRanges(left, right))
        {
            // Split each range around bytes a named field already reported.
            int start = -1;
            for (int i = offset; i <= offset + length; i++)
            {
                bool open = i < offset + length && !covered[i];
                if (open && start < 0) start = i;
                if (open || start < 0) continue;
                var range = new TagByteRange(start, i - start, Hex(left, start, i - start), Hex(right, start, i - start));
                start = -1;
                if (only is not null && !(range.Offset < only.Offset + only.Size && only.Offset < range.Offset + range.Length)) continue;
                count++;
                if (ranges.Count < MaximumRanges) ranges.Add(range);
            }
        }
        if (only is null && fieldFilter is { Length: > 0 } && ParseRange(fieldFilter) is { } wanted)
        {
            // A bare offset range filters byte ranges the same way.
            ranges.RemoveAll(range => !(range.Offset < wanted.Offset + wanted.Length && wanted.Offset < range.Offset + range.Length));
            count = ranges.Count;
            changed.RemoveAll(change => !(change.Field.Offset < wanted.Offset + wanted.Length && wanted.Offset < change.Field.Offset + change.Field.Size));
        }
        return new(diff, changed, ranges, count, fields);
    }

    /** A field by name (case-insensitive), by "0xOFFSET" or decimal offset of a known field, or an "OFFSET+LENGTH" byte range. */
    public static TagFieldSpan? Find(IReadOnlyList<TagFieldSpan> fields, string text)
    {
        text = text.Trim();
        if (fields.FirstOrDefault(field => string.Equals(field.Name, text, StringComparison.OrdinalIgnoreCase)) is { } named) return named;
        if (ParseRange(text) is not { } range) return null;
        if (!text.Contains('+') && fields.FirstOrDefault(field => field.Offset == range.Offset) is { } atOffset) return atOffset;
        return text.Contains('+') ? new($"0x{range.Offset:X}+{range.Length}", range.Offset, range.Length, "bytes", "range") : null;
    }

    /** "0x40", "64", "0x40+8" or "0x40+0x8": offset and length (length 4 when omitted). */
    public static (int Offset, int Length)? ParseRange(string text)
    {
        string[] parts = text.Trim().Split('+', 2);
        if (Number(parts[0]) is not { } offset || offset < 0) return null;
        int length = 4;
        if (parts.Length == 2 && (Number(parts[1]) is not { } given || (length = given) <= 0)) return null;
        return (offset, length);
    }

    /**
     * The authored bytes with one field (or byte range) copied back from base. A field past the end of either side
     * cannot be reverted alone: use a whole-tag revert instead.
     */
    public static byte[] RevertField(byte[] baseBytes, byte[] authored, TagFieldSpan field)
    {
        ArgumentNullException.ThrowIfNull(baseBytes);
        ArgumentNullException.ThrowIfNull(authored);
        ArgumentNullException.ThrowIfNull(field);
        if (field.Offset < 0 || field.Size <= 0 || field.Offset + field.Size > baseBytes.Length || field.Offset + field.Size > authored.Length)
            throw new InvalidOperationException($"{field.Name} lies past the end of the base or authored tag; revert the whole tag instead.");
        byte[] result = (byte[])authored.Clone();
        Array.Copy(baseBytes, field.Offset, result, field.Offset, field.Size);
        return result;
    }

    /** Merged runs of differing bytes; a size change adds the tail as one run. */
    public static IEnumerable<(int Offset, int Length)> ChangedRanges(byte[] left, byte[] right)
    {
        int common = Math.Min(left.Length, right.Length), longest = Math.Max(left.Length, right.Length);
        int start = -1;
        for (int i = 0; i < common; i++)
        {
            bool differs = left[i] != right[i];
            if (differs && start < 0) start = i;
            else if (!differs && start >= 0) { yield return (start, i - start); start = -1; }
        }
        if (start >= 0 && longest == common) yield return (start, common - start);
        else if (longest > common) yield return (start >= 0 ? start : common, longest - (start >= 0 ? start : common));
    }

    /** The field's value in data, or null when it lies past the end. */
    public static string? Format(byte[] data, TagFieldSpan field)
    {
        if (field.Offset < 0 || field.Offset + field.Size > data.Length) return null;
        var span = data.AsSpan(field.Offset, field.Size);
        var c = CultureInfo.InvariantCulture;
        return (field.Kind.ToLowerInvariant(), field.Size) switch
        {
            ("u8", 1) => span[0].ToString(c),
            ("i16", 2) => BinaryPrimitives.ReadInt16LittleEndian(span).ToString(c),
            ("u16", 2) => BinaryPrimitives.ReadUInt16LittleEndian(span).ToString(c),
            ("i32", 4) => BinaryPrimitives.ReadInt32LittleEndian(span).ToString(c),
            ("u32", 4) => BinaryPrimitives.ReadUInt32LittleEndian(span).ToString(c),
            ("hash", 4) => "0x" + BinaryPrimitives.ReadUInt32LittleEndian(span).ToString("X8", c),
            ("i64", 8) => BinaryPrimitives.ReadInt64LittleEndian(span).ToString(c),
            ("u64", 8) => BinaryPrimitives.ReadUInt64LittleEndian(span).ToString(c),
            ("f32", 4) => BinaryPrimitives.ReadSingleLittleEndian(span).ToString("G7", c),
            _ => Convert.ToHexString(span)
        };
    }

    private static bool Differs(byte[] left, byte[] right, int offset, int size)
    {
        bool inLeft = offset + size <= left.Length, inRight = offset + size <= right.Length;
        if (inLeft != inRight) return true;
        return inLeft && !left.AsSpan(offset, size).SequenceEqual(right.AsSpan(offset, size));
    }

    private static string? Hex(byte[] data, int offset, int length)
    {
        if (offset >= data.Length) return null;
        int shown = Math.Min(Math.Min(length, data.Length - offset), MaximumRangeBytes);
        return Convert.ToHexString(data, offset, shown) + (shown < Math.Min(length, data.Length - offset) ? "..." : "");
    }

    private static int? Number(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) ? hex : null;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : null;
    }
}
