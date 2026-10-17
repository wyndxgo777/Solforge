using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SunriseStudio.Packages.TagSchemas;

/** One decoded field: its schema row, absolute offset, numeric value (when it has one) and printed text. */
public sealed record TagFieldValue(TagSchemaField Field, int Offset, ulong? Raw, double? Number, string Text)
{
    /** Number as hex for hash-like types, decimal otherwise. */
    public override string ToString() => Text;
}

/** One validation finding: dotted field path, absolute offset and message. Error is false for warnings. */
public sealed record TagSchemaIssue(string Path, int Offset, string Message, bool Error);

/** A typed array as its descriptor and header describe it. */
public readonly record struct TagArrayView(int Field, int Count, uint Class, int Header, int Data);

/**
 * Reads tag bytes through a schema: scalar fields, relative pointers, typed arrays (element class and bounds), C strings.
 * Validation checks each field's bytes are inside the tag, its type (tag references in the 0x80800000+ space, finite
 * floats, booleans 0/1), its range (min/max with allowed sentinels) and arrays (marker, repeated count, element class,
 * count * stride inside the tag, max_count), recursing into element and pointer-target schemas.
 */
public static class TagSchemaReader
{
    public const uint ArrayMarker = 0x80809FBD;
    private const int MaximumArray = 1_048_576;

    /** Reads the typed array whose 16-byte descriptor is at field; problem says why it is not one. Count 0 is a valid empty array. */
    public static bool TryArray(ReadOnlySpan<byte> bytes, int field, out TagArrayView view, out string? problem)
    {
        view = default; problem = null;
        if (field < 0 || field + 16 > bytes.Length) { problem = "array descriptor leaves the tag"; return false; }
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(bytes[field..]);
        long delta = BinaryPrimitives.ReadInt64LittleEndian(bytes[(field + 8)..]);
        if (count == 0) { view = new(field, 0, 0, -1, -1); return true; }
        if (count > MaximumArray) { problem = $"count {count} is not plausible"; return false; }
        long header = field + 8L + delta;
        if (header < 4 || header + 16 > bytes.Length) { problem = "array header leaves the tag"; return false; }
        int at = (int)header;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at - 4)..]) != ArrayMarker) { problem = "array header has no 0x80809FBD marker"; return false; }
        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]) != count) { problem = "array header count disagrees with the descriptor"; return false; }
        view = new(field, (int)count, BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 8)..]), at, at + 16);
        return true;
    }

    /** Resolves a signed relative pointer (from the pointer word); false when it is zero or leaves the tag. */
    public static bool TryRelative(ReadOnlySpan<byte> bytes, int field, out int target)
    {
        target = -1;
        if (field < 0 || field + 8 > bytes.Length) return false;
        long delta = BinaryPrimitives.ReadInt64LittleEndian(bytes[field..]);
        if (delta == 0) return false;
        long result = field + delta;
        if (result < 0 || result >= bytes.Length) return false;
        target = (int)result;
        return true;
    }

    /** A NUL-terminated ASCII string at offset (at most max bytes), or null when unterminated or not ASCII. */
    public static string? CString(ReadOnlySpan<byte> bytes, int offset, int max = 256)
    {
        if (offset < 0 || offset >= bytes.Length) return null;
        int end = Math.Min(bytes.Length, offset + max);
        for (int i = offset; i < end; i++)
        {
            if (bytes[i] == 0) return Encoding.ASCII.GetString(bytes[offset..i]);
            if (bytes[i] > 0x7E || bytes[i] < 0x20) return null;
        }
        return null;
    }

    /** Reads one field of the struct at start; null when its bytes leave the tag. */
    public static TagFieldValue? Read(ReadOnlySpan<byte> bytes, int start, TagSchemaField field)
    {
        int at = start + field.Offset;
        if (at < 0 || at + field.Width > bytes.Length) return null;
        switch (field.Type)
        {
            case "u8": case "bool8": { byte value = bytes[at]; return new(field, at, value, value, value.ToString(CultureInfo.InvariantCulture)); }
            case "i8": { sbyte value = (sbyte)bytes[at]; return new(field, at, (ulong)(long)value, value, value.ToString(CultureInfo.InvariantCulture)); }
            case "u16": { ushort value = BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]); return new(field, at, value, value, value.ToString(CultureInfo.InvariantCulture)); }
            case "i16": { short value = BinaryPrimitives.ReadInt16LittleEndian(bytes[at..]); return new(field, at, (ulong)(long)value, value, value.ToString(CultureInfo.InvariantCulture)); }
            case "i32": { int value = BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]); return new(field, at, (ulong)(long)value, value, value.ToString(CultureInfo.InvariantCulture)); }
            case "u64": case "size64": { ulong value = BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]); return new(field, at, value, value, value.ToString(CultureInfo.InvariantCulture)); }
            case "i64": { long value = BinaryPrimitives.ReadInt64LittleEndian(bytes[at..]); return new(field, at, (ulong)value, value, value.ToString(CultureInfo.InvariantCulture)); }
            case "f32":
            {
                float value = BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]);
                return new(field, at, BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]), value, value.ToString("R", CultureInfo.InvariantCulture));
            }
            case "hash": case "tag": case "class": case "string_hash":
            { uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]); return new(field, at, value, value, $"0x{value:X8}"); }
            case "string_ref": case "string_ref16":
            {
                uint bank = field.Type == "string_ref" ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
                uint hash = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);
                return new(field, at, hash, hash, $"bank {bank} 0x{hash:X8}");
            }
            case "relptr": case "relptr_tag": case "relptr_cstring":
            {
                if (!TryRelative(bytes, at, out int target)) return new(field, at, null, null, "null");
                if (field.Type == "relptr_tag")
                {
                    if (target + 4 > bytes.Length) return new(field, at, null, null, "out of range");
                    uint tag = BinaryPrimitives.ReadUInt32LittleEndian(bytes[target..]);
                    return new(field, at, tag, tag, $"0x{tag:X8}");
                }
                if (field.Type == "relptr_cstring") return new(field, at, (ulong)target, target, CString(bytes, target) ?? "(bad string)");
                return new(field, at, (ulong)target, target, $"-> 0x{target:X}");
            }
            case "array":
                return TryArray(bytes, at, out var view, out string? problem)
                    ? new(field, at, (ulong)view.Count, view.Count, view.Count == 0 ? "0 elements" : $"{view.Count} x 0x{view.Class:X8} @0x{view.Data:X}")
                    : new(field, at, null, null, "(bad array: " + problem + ")");
            default:
            { uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]); return new(field, at, value, value, value.ToString(CultureInfo.InvariantCulture)); }
        }
    }

    /** Every field of the struct at start, in offset order (fields whose bytes leave the tag are skipped). */
    public static IEnumerable<TagFieldValue> ReadAll(byte[] bytes, int start, TagSchema schema)
    {
        foreach (var field in schema.Fields)
            if (Read(bytes, start, field) is { } value) yield return value;
    }

    /** Validates the struct at start against schema, recursing into elements and pointer targets up to depth levels. */
    public static List<TagSchemaIssue> Validate(TagSchemaRegistry registry, byte[] bytes, TagSchema schema, int start = 0, int depth = 3, int elementLimit = 4096)
    {
        var issues = new List<TagSchemaIssue>();
        Check(registry, bytes, schema, start, depth, elementLimit, schema.Id, issues);
        return issues;
    }

    private static void Check(TagSchemaRegistry registry, byte[] bytes, TagSchema schema, int start, int depth, int elementLimit, string path, List<TagSchemaIssue> issues)
    {
        if (issues.Count > 500) return;
        if (schema.Size > 0 && start + schema.Size > bytes.Length)
            issues.Add(new(path, start, $"struct of {schema.Size} bytes at 0x{start:X} runs past the tag end ({bytes.Length})", true));
        foreach (var field in schema.Fields)
        {
            string at = path + "." + field.Name;
            var value = Read(bytes, start, field);
            if (value is null) { issues.Add(new(at, start + field.Offset, "field leaves the tag", true)); continue; }
            CheckValue(field, value, at, issues);
            if (field.Type == "array") CheckArray(registry, bytes, field, start + field.Offset, depth, elementLimit, at, issues);
            else if (field.Type == "relptr" && field.Target is { } target && depth > 0 && registry.FindId(target) is { } targetSchema
                && TryRelative(bytes, start + field.Offset, out int pointed))
                Check(registry, bytes, targetSchema, pointed, depth - 1, elementLimit, at + "->", issues);
        }
    }

    private static void CheckValue(TagSchemaField field, TagFieldValue value, string path, List<TagSchemaIssue> issues)
    {
        if (value.Raw is not { } raw) return;
        bool allowed = field.Allow?.Contains(raw) == true;
        switch (field.Type)
        {
            case "tag" when !allowed && raw is not (0 or 0xFFFFFFFF) && (raw < 0x80800000 || raw >= 0xC0000000):
                issues.Add(new(path, value.Offset, $"{value.Text} is not a tag reference", true)); break;
            case "class" when !allowed && (raw < 0x80800000 || raw > 0x8081FFFF):
                issues.Add(new(path, value.Offset, $"{value.Text} is not a tag class", true)); break;
            case "f32" when value.Number is { } number && !double.IsFinite(number):
                issues.Add(new(path, value.Offset, "float is not finite", true)); break;
            case "bool8" when raw > 1:
                issues.Add(new(path, value.Offset, $"boolean byte is {raw}", true)); break;
        }
        if (field.Classes is { Count: > 0 } && field.Type is "tag" or "relptr_tag" && !allowed && raw is not (0 or 0xFFFFFFFF))
            issues.Add(new(path, value.Offset, $"{value.Text} should be a tag of class {string.Join("/", field.Classes.Select(type => $"0x{type:X8}"))} (checked by the caller with the catalog)", false));
        if (allowed || value.Number is not { } n || field.Type == "array") return;
        if (field.Min is { } min && n < min) issues.Add(new(path, value.Offset, $"{value.Text} is below the minimum {min.ToString(CultureInfo.InvariantCulture)}", true));
        if (field.Max is { } max && n > max) issues.Add(new(path, value.Offset, $"{value.Text} is above the maximum {max.ToString(CultureInfo.InvariantCulture)}", true));
    }

    private static void CheckArray(TagSchemaRegistry registry, byte[] bytes, TagSchemaField field, int at, int depth, int elementLimit, string path, List<TagSchemaIssue> issues)
    {
        if (!TryArray(bytes, at, out var view, out string? problem)) { issues.Add(new(path, at, problem ?? "bad array", true)); return; }
        if (view.Count == 0) return;
        if (field.ElementClass is { } expected && view.Class != expected)
            issues.Add(new(path, at, $"element class 0x{view.Class:X8}, schema says 0x{expected:X8}", true));
        if (field.MaxCount > 0 && view.Count > field.MaxCount)
            issues.Add(new(path, at, $"{view.Count} elements, schema allows at most {field.MaxCount}", true));
        var element = field.Element is { } id ? registry.FindId(id) : null;
        int stride = field.Stride > 0 ? field.Stride : element?.Size ?? 0;
        if (stride > 0 && (long)view.Data + (long)view.Count * stride > bytes.Length)
        { issues.Add(new(path, at, $"{view.Count} x {stride} bytes from 0x{view.Data:X} run past the tag end ({bytes.Length})", true)); return; }
        if (element is null || stride <= 0 || depth <= 0) return;
        for (int i = 0; i < Math.Min(view.Count, elementLimit); i++)
            Check(registry, bytes, element, view.Data + i * stride, depth - 1, elementLimit, $"{path}[{i}]", issues);
    }

    /**
     * The values a field path reaches from the struct at start. A path is dotted field names; "name[]" walks every
     * element of an array (its element schema), "name->" follows a relative pointer to its target schema. Each result
     * carries the concrete path with element indexes.
     */
    public static IEnumerable<(string Path, TagFieldValue Value)> Values(TagSchemaRegistry registry, byte[] bytes, TagSchema schema, int start, string fieldPath, int elementLimit = 100_000)
    {
        string[] parts = fieldPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? [] : Walk(registry, bytes, schema, start, parts, 0, "", elementLimit);
    }

    private static IEnumerable<(string, TagFieldValue)> Walk(TagSchemaRegistry registry, byte[] bytes, TagSchema schema, int start, string[] parts, int index, string prefix, int elementLimit)
    {
        string part = parts[index];
        bool each = part.EndsWith("[]", StringComparison.Ordinal), follow = part.EndsWith("->", StringComparison.Ordinal);
        string name = each || follow ? part[..^2] : part;
        if (schema.Field(name) is not { } field) yield break;
        string here = prefix.Length == 0 ? field.Name : prefix + "." + field.Name;
        if (index == parts.Length - 1 && !each && !follow)
        {
            if (Read(bytes, start, field) is { } value) yield return (here, value);
            yield break;
        }
        if (each && field.Type == "array" && field.Element is { } id && registry.FindId(id) is { } element
            && TryArray(bytes, start + field.Offset, out var view, out _) && view.Count > 0)
        {
            int stride = field.Stride > 0 ? field.Stride : element.Size;
            if (stride <= 0) yield break;
            for (int i = 0; i < Math.Min(view.Count, elementLimit); i++)
            {
                int item = view.Data + i * stride;
                if (item + stride > bytes.Length) yield break;
                if (index == parts.Length - 1) continue;
                foreach (var hit in Walk(registry, bytes, element, item, parts, index + 1, $"{here}[{i}]", elementLimit)) yield return hit;
            }
        }
        else if (follow && field.Type == "relptr" && field.Target is { } target && registry.FindId(target) is { } targetSchema
            && TryRelative(bytes, start + field.Offset, out int pointed) && index < parts.Length - 1)
            foreach (var hit in Walk(registry, bytes, targetSchema, pointed, parts, index + 1, here + "->", elementLimit)) yield return hit;
    }

    /**
     * Whether a value matches: op is eq (default), ne, lt, le, gt, ge or contains. Numbers compare numerically when the
     * query parses (decimal or 0x hex); otherwise text compares case-insensitively.
     */
    public static bool Matches(TagFieldValue value, string op, string query)
    {
        query = query.Trim();
        double? number = ParseNumber(query);
        if (op == "contains") return value.Text.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (number is { } wanted && value.Number is { } actual)
            return op switch
            {
                "ne" => actual != wanted, "lt" => actual < wanted, "le" => actual <= wanted, "gt" => actual > wanted, "ge" => actual >= wanted,
                _ => actual == wanted
            };
        bool same = string.Equals(value.Text, query, StringComparison.OrdinalIgnoreCase);
        return op == "ne" ? !same : op is "eq" or "" && same;
    }

    private static double? ParseNumber(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex) ? hex : null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
    }
}
