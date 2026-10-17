using System.Globalization;
using System.Text.Json;

namespace SunriseStudio.Packages.TagSchemas;

/**
 * One named struct layout from the in-repo schema files (TagSchemas/schemas/*.json). A schema is found by its tag class
 * hash, or by its id when the struct has no class of its own (a record inside a table that a relative pointer reaches).
 * Every field says where its offset and meaning come from (Source) and whether that is confirmed or a guess (Status).
 */
public sealed record TagSchema(
    string Id,
    uint? Class,
    string Name,
    string Family,
    int Size,
    string Provenance,
    IReadOnlyList<string> Decoders,
    IReadOnlyList<TagSchemaField> Fields,
    string File)
{
    /** The field with this name (case-insensitive), or null. */
    public TagSchemaField? Field(string name) =>
        Fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase));

    /** Offset of a field that must exist; throws when the schema file lost it, so decoders fail loudly. */
    public int Offset(string name) => Field(name)?.Offset
        ?? throw new InvalidOperationException($"Tag schema {Id} has no field '{name}' ({File}).");

    /** Fields whose status is guessed. */
    public IEnumerable<TagSchemaField> Guessed => Fields.Where(field => !field.Confirmed);
}

/**
 * One field of a schema. Type is one of TagFieldTypes. Arrays name their element class (checked against the array
 * header) and optionally an element schema id and stride. Min/Max bound numeric values; Allow lists sentinel values
 * (0xFFFFFFFF, 0xFFFF...) that pass the range check. Target names the schema a relative pointer reaches.
 */
public sealed record TagSchemaField(
    string Name,
    int Offset,
    string Type,
    string Status,
    string Source,
    string? Note = null,
    uint? ElementClass = null,
    string? Element = null,
    int Stride = 0,
    int MaxCount = 0,
    double? Min = null,
    double? Max = null,
    IReadOnlyList<ulong>? Allow = null,
    string? Target = null,
    IReadOnlyList<uint>? Classes = null)
{
    public bool Confirmed => string.Equals(Status, "confirmed", StringComparison.OrdinalIgnoreCase);
    /** Bytes the field itself occupies at its offset. */
    public int Width => TagFieldTypes.Width(Type);
}

/** The field types a schema may use and their widths. */
public static class TagFieldTypes
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "u8", "u16", "u32", "u64", "i8", "i16", "i32", "i64", "f32", "bool8",
        // u32 values that name things: a hash (FNV-1 or Bungie hash), a tag reference, a tag class, a localized string hash.
        "hash", "tag", "class", "string_hash",
        // localized text: a u32 (or u16 + 2 padding) string-bank index followed by a u32 string hash.
        "string_ref", "string_ref16",
        // relative pointers (signed 64-bit, from the pointer word): to a struct (Target), to a u32 tag, to a C string.
        "relptr", "relptr_tag", "relptr_cstring",
        // typed array: u64 count + relative u64 to a header after a 0x80809FBD marker that repeats count and holds the element class.
        "array",
        // u64 file size at the start of a tag.
        "size64"
    };

    /** Width in bytes of a field of this type. */
    public static int Width(string type) => type switch
    {
        "u8" or "i8" or "bool8" => 1,
        "u16" or "i16" => 2,
        "u64" or "i64" or "relptr" or "relptr_tag" or "relptr_cstring" or "string_ref" or "string_ref16" or "size64" => 8,
        "array" => 16,
        _ => 4
    };
}

/** Parses schema JSON documents ({"schemas": [...]}) into TagSchema rows, collecting problems instead of throwing. */
public static class TagSchemaJson
{
    /** Parses one file's text; problems names the file and the bad entry. */
    public static List<TagSchema> Parse(string file, string json, List<string> problems)
    {
        var schemas = new List<TagSchema>();
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException error) { problems.Add($"{file}: {error.Message}"); return schemas; }
        using (document)
        {
            if (!document.RootElement.TryGetProperty("schemas", out var list) || list.ValueKind != JsonValueKind.Array)
            { problems.Add($"{file}: no \"schemas\" array."); return schemas; }
            foreach (var entry in list.EnumerateArray())
            {
                string id = Text(entry, "id") ?? "";
                try { schemas.Add(ReadSchema(file, entry)); }
                catch (Exception error) when (error is FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
                { problems.Add($"{file}: schema '{id}': {error.Message}"); }
            }
        }
        return schemas;
    }

    private static TagSchema ReadSchema(string file, JsonElement entry)
    {
        uint? type = Hex(entry, "class");
        string id = Text(entry, "id") ?? (type is { } value ? $"0x{value:X8}" : throw new FormatException("needs an id or a class"));
        var fields = new List<TagSchemaField>();
        if (entry.TryGetProperty("fields", out var list))
            foreach (var field in list.EnumerateArray()) fields.Add(ReadField(field));
        foreach (var field in fields)
        {
            if (!TagFieldTypes.All.Contains(field.Type)) throw new FormatException($"field '{field.Name}' has unknown type '{field.Type}'");
            if (field.Offset < 0) throw new FormatException($"field '{field.Name}' has a negative offset");
            if (field.Status is not ("confirmed" or "guessed")) throw new FormatException($"field '{field.Name}' status must be confirmed or guessed");
            if (string.IsNullOrWhiteSpace(field.Source)) throw new FormatException($"field '{field.Name}' has no source");
        }
        var duplicate = fields.GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new FormatException($"field '{duplicate.Key}' is declared twice");
        int size = entry.TryGetProperty("size", out var sizeValue) ? sizeValue.GetInt32() : 0;
        string provenance = Text(entry, "provenance") ?? throw new FormatException("needs provenance");
        var decoders = entry.TryGetProperty("decoders", out var used) ? used.EnumerateArray().Select(item => item.GetString() ?? "").ToList() : [];
        return new TagSchema(id, type, Text(entry, "name") ?? id, Text(entry, "family") ?? "", size, provenance, decoders,
            fields.OrderBy(field => field.Offset).ToList(), file);
    }

    private static TagSchemaField ReadField(JsonElement field)
    {
        string name = Text(field, "name") ?? throw new FormatException("a field has no name");
        int offset = field.TryGetProperty("offset", out var at) ? Number(at) : throw new FormatException($"field '{name}' has no offset");
        IReadOnlyList<ulong>? allow = field.TryGetProperty("allow", out var values)
            ? values.EnumerateArray().Select(value => (ulong)NumberLong(value)).ToList() : null;
        IReadOnlyList<uint>? classes = field.TryGetProperty("classes", out var types)
            ? types.EnumerateArray().Select(value => (uint)NumberLong(value)).ToList() : null;
        return new TagSchemaField(name, offset, Text(field, "type") ?? "u32", Text(field, "status") ?? "guessed", Text(field, "source") ?? "",
            Text(field, "note"), Hex(field, "element_class"), Text(field, "element"),
            field.TryGetProperty("stride", out var stride) ? stride.GetInt32() : 0,
            field.TryGetProperty("max_count", out var maxCount) ? maxCount.GetInt32() : 0,
            field.TryGetProperty("min", out var min) ? min.GetDouble() : null,
            field.TryGetProperty("max", out var max) ? max.GetDouble() : null,
            allow, Text(field, "target"), classes);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static uint? Hex(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? (uint)NumberLong(value) : null;

    private static int Number(JsonElement value) => checked((int)NumberLong(value));

    /** A JSON number, or a string holding decimal or 0x hex. */
    private static long NumberLong(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.GetInt64();
        string text = value.GetString()?.Trim() ?? throw new FormatException("expected a number");
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(text, CultureInfo.InvariantCulture);
    }
}
