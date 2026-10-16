using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Solforge.Mcp.Data;

/** One tag as the package catalog lists it. */
public sealed record TagSummary(uint Hash, string? Name, uint Class, string ClassName, string Package, int Size);

/**
 * Package and string access the data tools need, implemented by the Solforge editor over its open installation. Kept
 * separate from ISolforgeHost so hosts without an installation (the checks, the stdio shim) do not have to implement it.
 */
public interface ISolforgeDataHost
{
    /** Why package data is unavailable (no installation open, still loading), or null when it is ready. */
    string? DataUnavailable { get; }
    TagSummary? Tag(uint hash);
    bool TagExists(uint hash);
    /** The tag's bytes with project edits applied, or the shipped bytes when original is true; null for an unknown tag. */
    byte[]? ReadTag(uint hash, bool original);
    /** Every tag whose class reference is classHash, in catalog order. */
    IEnumerable<TagSummary> TagsOfClass(uint classHash);
    /** Solforge's class label table (reference hash to label) and counts of tags per class in the installation. */
    IReadOnlyDictionary<uint, string> ClassLabels { get; }
    IReadOnlyDictionary<uint, int> ClassCounts();
    /** Localized strings: every candidate text for a string hash (one when unique); loads all banks on first use. */
    Task<IReadOnlyList<string>> StringsAsync(uint hash, CancellationToken cancel);
    Task<IReadOnlyList<(uint Hash, string Text, uint? Container)>> SearchStringsAsync(string query, int limit, CancellationToken cancel);
    /** Synchronous lookup once strings are loaded (null before); used while walking tag bytes. */
    string? StringIfLoaded(uint hash);
}

/**
 * Reads a Tiger tag's bytes without a hand-written decoder: typed arrays (u64 count + relative u64 to a header that
 * follows a 0x80809FBD marker and holds count and element class), tag references (u32 hashes the catalog knows),
 * localized string hashes, class hashes, floats and integers. infer_tag_schema runs this over many tags of one class and
 * keeps what agrees, which gives a struct layout for classes Solforge has no decoder for.
 */
public static class TagLayout
{
    public const uint ArrayMarker = 0x80809FBD;
    private const int MaximumCount = 1_048_576;

    public sealed record Field(int Offset, string Kind, string Value);
    public sealed record ArrayInfo(int Offset, int Count, uint Class, int Data, int Stride);

    /** The root struct's fields, then each array (depth-first up to maxDepth) with its first elements' fields. */
    public static JsonObject Describe(byte[] bytes, ISolforgeDataHost? host, int maxDepth = 2, int elementsPerArray = 3)
    {
        var markers = Markers(bytes);
        int rootEnd = markers.Count > 0 ? Math.Min(bytes.Length, markers[0] - 4) : bytes.Length;
        return new JsonObject
        {
            ["size"] = bytes.Length,
            ["root_size"] = rootEnd,
            ["root"] = Struct(bytes, 0, rootEnd, markers, host, maxDepth, elementsPerArray)
        };
    }

    /** Offsets of every array header (the byte after each 0x80809FBD marker). */
    public static List<int> Markers(byte[] bytes)
    {
        var found = new List<int>();
        for (int i = 0; i + 4 <= bytes.Length; i += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == ArrayMarker) found.Add(i + 4);
        return found;
    }

    public static ArrayInfo? TryArray(byte[] bytes, int field, IReadOnlyList<int> markers)
    {
        if (field < 0 || field + 16 > bytes.Length || (field & 7) != 0) return null;
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(field));
        if (count is 0 or > MaximumCount) return null;
        long delta = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field + 8));
        long header = field + 8 + delta;
        if (header < 4 || header + 16 > bytes.Length) return null;
        int at = (int)header;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at - 4)) != ArrayMarker) return null;
        if (BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at)) != count) return null;
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 8));
        int data = at + 16;
        int next = bytes.Length;
        foreach (int marker in markers) if (marker - 4 >= data) { next = marker - 4; break; }
        int stride = (int)Math.Max(0, (next - data) / (long)count);
        return new ArrayInfo(field, (int)count, type, data, stride);
    }

    private static JsonArray Struct(byte[] bytes, int start, int end, IReadOnlyList<int> markers, ISolforgeDataHost? host, int depth, int elements)
    {
        var rows = new JsonArray();
        for (int offset = start; offset + 4 <= end;)
        {
            if (((offset - start) & 7) == 0 && TryArray(bytes, offset, markers) is { } array)
            {
                var row = new JsonObject
                {
                    ["offset"] = Hex(offset - start), ["kind"] = "array", ["count"] = array.Count,
                    ["class"] = Hex(array.Class), ["class_name"] = Label(host, array.Class), ["data"] = Hex(array.Data), ["stride"] = array.Stride
                };
                if (depth > 0 && array.Stride >= 4)
                {
                    var items = new JsonArray();
                    for (int i = 0; i < Math.Min(array.Count, elements); i++)
                    {
                        int itemStart = array.Data + i * array.Stride;
                        items.Add(Struct(bytes, itemStart, Math.Min(bytes.Length, itemStart + array.Stride), markers, host, depth - 1, elements));
                    }
                    row["elements"] = items;
                }
                rows.Add(row);
                offset += 16;
                continue;
            }
            var field = Word(bytes, offset, host);
            rows.Add(new JsonObject { ["offset"] = Hex(offset - start), ["kind"] = field.Kind, ["value"] = field.Value });
            offset += 4;
        }
        return rows;
    }

    /** What a 4-byte word most likely is. */
    public static Field Word(byte[] bytes, int offset, ISolforgeDataHost? host)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        if (value == 0) return new(offset, "zero", "0");
        if (value == 0xFFFFFFFF) return new(offset, "none", "-1");
        if (host is not null && (value & 0x80000000) != 0 && host.TagExists(value))
        {
            var tag = host.Tag(value);
            return new(offset, "tag", $"0x{value:X8} {tag?.ClassName} {tag?.Name}".TrimEnd());
        }
        if (host?.ClassLabels.TryGetValue(value, out string? label) == true) return new(offset, "class", $"0x{value:X8} {label}");
        if (Decomp.DecompIndex.IsTagClass(value)) return new(offset, "class", $"0x{value:X8}");
        if (host?.StringIfLoaded(value) is { } text) return new(offset, "string", $"0x{value:X8} \"{(text.Length > 80 ? text[..80] + "..." : text)}\"");
        float single = BitConverter.Int32BitsToSingle((int)value);
        int exponent = (int)((value >> 23) & 0xFF);
        if (exponent is > 100 and < 160 && float.IsFinite(single)) return new(offset, "f32", single.ToString("R", CultureInfo.InvariantCulture));
        if (value < 0x10000) return new(offset, "u32", value.ToString(CultureInfo.InvariantCulture));
        if ((value & 0xFFFF) < 0x100 && value >> 16 < 0x100) return new(offset, "u16x2", $"{value & 0xFFFF}, {value >> 16}");
        return new(offset, "hash", $"0x{value:X8}");
    }

    /**
     * A layout for a whole class: walks the root struct of up to sample tags and, per offset, keeps how often each kind
     * appears and a few example values. Offsets every sampled tag agrees on are marked stable.
     */
    public static JsonObject InferSchema(IEnumerable<(TagSummary Tag, byte[] Bytes)> samples, ISolforgeDataHost? host)
    {
        var kinds = new SortedDictionary<int, Dictionary<string, int>>();
        var examples = new Dictionary<int, List<string>>();
        var arrays = new Dictionary<int, HashSet<uint>>();
        int count = 0, smallest = int.MaxValue, largest = 0;
        foreach (var (_, bytes) in samples)
        {
            count++;
            var markers = Markers(bytes);
            int rootEnd = markers.Count > 0 ? Math.Min(bytes.Length, markers[0] - 4) : bytes.Length;
            smallest = Math.Min(smallest, rootEnd); largest = Math.Max(largest, rootEnd);
            for (int offset = 0; offset + 4 <= rootEnd;)
            {
                string kind, value;
                int step = 4;
                if ((offset & 7) == 0 && TryArray(bytes, offset, markers) is { } array)
                {
                    kind = "array"; value = $"{array.Count} x 0x{array.Class:X8} stride {array.Stride}"; step = 16;
                    if (!arrays.TryGetValue(offset, out var classes)) arrays[offset] = classes = [];
                    classes.Add(array.Class);
                }
                else { var field = Word(bytes, offset, host); kind = field.Kind; value = field.Value; }
                if (!kinds.TryGetValue(offset, out var seen)) kinds[offset] = seen = [];
                seen[kind] = seen.GetValueOrDefault(kind) + 1;
                if (!examples.TryGetValue(offset, out var list)) examples[offset] = list = [];
                if (list.Count < 3 && !list.Contains(value)) list.Add(value);
                offset += step;
            }
        }
        var fields = new JsonArray();
        foreach (var (offset, seen) in kinds)
        {
            var best = seen.Where(pair => pair.Key is not ("zero" or "none")).OrderByDescending(pair => pair.Value).FirstOrDefault();
            string kind = best.Key ?? (seen.ContainsKey("zero") ? "zero" : "none");
            var row = new JsonObject
            {
                ["offset"] = Hex(offset), ["kind"] = kind, ["seen"] = seen.Values.Sum(), ["of"] = count,
                ["stable"] = seen.Count == 1 || seen.Keys.All(key => key == kind || key is "zero" or "none"),
                ["kinds"] = new JsonObject(seen.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
                ["examples"] = new JsonArray([.. examples[offset].Select(example => (JsonNode?)example)])
            };
            if (arrays.TryGetValue(offset, out var classes))
                row["array_classes"] = new JsonArray([.. classes.Select(type => (JsonNode?)$"0x{type:X8} {Label(host, type)}".TrimEnd())]);
            fields.Add(row);
        }
        return new JsonObject
        {
            ["samples"] = count, ["root_size_min"] = count == 0 ? 0 : smallest, ["root_size_max"] = largest, ["fields"] = fields
        };
    }

    private static string? Label(ISolforgeDataHost? host, uint type) => host?.ClassLabels.GetValueOrDefault(type);
    private static string Hex(int value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);
    private static string Hex(uint value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
}
