/**
 * File: Retail (245048) class <-> Sol (86657) class correspondence, embedded as Data/class_map.json. Generated from
 * Charm's per-strategy schema attributes (Shadowkeep 2601/2999 = the 86657 family; Beyond Light, Witch Queen and Latest
 * = retail), alkahest's tiger_type ids and the retail tag counts. Every row carries its evidence.
 */
using System.Globalization;
using System.Text.Json;

namespace Solforge.Retail;

/**
 * One correspondence. Status: corroborated (Charm names both classes and alkahest or the dump confirms the retail one),
 * charm_only (Charm names both, nothing else confirms), retail_only (no 86657 class is known), sol_only (no retail class).
 */
public sealed record RetailClassRow(string Name, string? Charm, uint? SolClass, int? SolSize, uint? RetailClass, int? RetailSize,
    int RetailTags, string Status, IReadOnlyList<string> Evidence);

/** The embedded class map. */
public sealed class RetailClassMap
{
    private readonly Dictionary<uint, List<RetailClassRow>> byRetail = new();
    private readonly Dictionary<uint, List<RetailClassRow>> bySol = new();

    public IReadOnlyList<RetailClassRow> Rows { get; }
    public string Source { get; }

    private RetailClassMap(List<RetailClassRow> rows, string source)
    {
        Rows = rows; Source = source;
        foreach (var row in rows)
        {
            if (row.RetailClass is { } retail) Add(byRetail, retail, row);
            if (row.SolClass is { } sol) Add(bySol, sol, row);
        }
        static void Add(Dictionary<uint, List<RetailClassRow>> map, uint key, RetailClassRow row)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(row);
        }
    }

    private static readonly Lazy<RetailClassMap> Embedded = new(() =>
    {
        using var stream = typeof(RetailClassMap).Assembly.GetManifestResourceStream("Solforge.Retail.class_map.json")
            ?? throw new InvalidOperationException("Missing resource Solforge.Retail.class_map.json.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    /** The map shipped with this build. */
    public static RetailClassMap Default => Embedded.Value;

    /** Rows naming a retail class (several Charm structs can share one). */
    public IReadOnlyList<RetailClassRow> ForRetail(uint retailClass) => byRetail.TryGetValue(retailClass, out var list) ? list : [];
    /** Rows naming a Sol (86657) class. */
    public IReadOnlyList<RetailClassRow> ForSol(uint solClass) => bySol.TryGetValue(solClass, out var list) ? list : [];
    /** The 86657 class a retail class maps to when the map names exactly one, else null. */
    public uint? SolClassOf(uint retailClass) =>
        ForRetail(retailClass).Select(row => row.SolClass).Where(value => value is not null).Distinct().ToList() is [{ } only] ? only : null;
    /** A display name for a retail class, or null. */
    public string? NameOf(uint retailClass) => ForRetail(retailClass).FirstOrDefault()?.Name;

    /** Parses class_map.json ({"source": "...", "rows": [...]}). */
    public static RetailClassMap Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var rows = new List<RetailClassRow>();
        foreach (var row in root.GetProperty("rows").EnumerateArray())
            rows.Add(new RetailClassRow(Text(row, "name") ?? "?", Text(row, "charm"), Hex(row, "sol_class"), Int(row, "sol_size"),
                Hex(row, "retail_class"), Int(row, "retail_size"), Int(row, "retail_tags") ?? 0, Text(row, "status") ?? "charm_only",
                row.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array
                    ? evidence.EnumerateArray().Select(item => item.GetString() ?? "").ToList() : []));
        return new RetailClassMap(rows, Text(root, "source") ?? "");
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static uint? Hex(JsonElement element, string name) => Text(element, name) is { } text
        && uint.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
        ? value : null;
}
