using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Solforge.Mcp.Data;

/**
 * Cursor paging for the data tools. A cursor is the decimal row number to start at, returned as next_cursor; it stays
 * valid because every list is sorted deterministically (RVA, hash, offset). Plain offsets are accepted too.
 */
public static class DataPages
{
    public const int DefaultLimit = 50, MaximumLimit = 500;

    /** First row of the page: the cursor if given, else the offset argument. */
    public static int Start(JsonObject args)
    {
        string? cursor = McpJson.String(args, "cursor");
        if (cursor is { Length: > 0 } && int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int at)) return at;
        return Math.Max(0, McpJson.Int(args, "offset", 0));
    }

    /** Page size from the limit argument, clamped to 1..MaximumLimit. */
    public static int Limit(JsonObject args, int fallback = DefaultLimit) => Math.Clamp(McpJson.Int(args, "limit", fallback), 1, MaximumLimit);

    /** One page with total (counted to the end unless countAll is false, for expensive scans). */
    public static JsonObject Page<T>(IEnumerable<T> rows, JsonObject args, Func<T, JsonNode?> row, bool countAll = true, int fallback = DefaultLimit)
    {
        int start = Start(args), limit = Limit(args, fallback), seen = 0;
        var items = new JsonArray();
        bool more = false;
        foreach (var item in rows)
        {
            if (seen >= start && items.Count < limit) items.Add(row(item));
            else if (seen >= start + limit) { more = true; if (!countAll) { seen++; break; } }
            seen++;
        }
        var page = new JsonObject { ["cursor"] = start.ToString(CultureInfo.InvariantCulture), ["count"] = items.Count };
        if (countAll) page["total"] = seen; else page["total_at_least"] = seen;
        page["next_cursor"] = more ? (start + items.Count).ToString(CultureInfo.InvariantCulture) : null;
        page["rows"] = items;
        return page;
    }

    /** The cursor and limit arguments every paged data tool takes. */
    public static readonly (string, string, string, bool)[] Args =
    [
        ("cursor", "string", "Paging cursor: pass the previous result's next_cursor to get the next page (omit for the first page)", false),
        ("limit", "integer", $"Rows per page (default {DefaultLimit}, max {MaximumLimit})", false)
    ];

    /** Rows of a page as CSV: columns are the union of the rows' keys; nested values are written as JSON. */
    public static string Csv(IReadOnlyList<JsonNode?> rows)
    {
        var columns = new List<string>();
        foreach (var row in rows.Take(2000))
            if (row is JsonObject item) foreach (var (key, _) in item) if (!columns.Contains(key)) columns.Add(key);
        if (columns.Count == 0) columns.Add("value");
        var text = new StringBuilder().AppendLine(string.Join(',', columns.Select(Cell)));
        foreach (var row in rows)
        {
            var cells = columns.Select(column => row is JsonObject item ? Value(item[column]) : column == "value" ? Value(row) : "");
            text.AppendLine(string.Join(',', cells.Select(Cell)));
        }
        return text.ToString();
        static string Value(JsonNode? node) => node switch
        {
            null => "",
            JsonValue value => value.ToString(),
            _ => node.ToJsonString()
        };
        static string Cell(string value) => value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
