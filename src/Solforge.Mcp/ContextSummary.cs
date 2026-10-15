using System.Text;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * The get_context snapshot as a few plain lines, for the Claude Code prompt hook (solforge-mcp --context).
 * Empty when context is paused or there is nothing to say. The shim links this file.
 */
public static class ContextSummary
{
    public const int Limit = 2048;

    public static string Format(JsonNode? context, int limit = Limit)
    {
        if (context is not JsonObject root || root["paused"]?.GetValueKind() == System.Text.Json.JsonValueKind.True) return "";
        var lines = new List<string>();
        void Line(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{name}: {value}"); }
        string? Text(JsonNode? node) => node is JsonValue value ? value.ToString() : null;
        string Item(JsonNode? node) => $"{Text(node?["label"])} [{Text(node?["id"])}]";

        Line("Map", Join(Text(root["map"]), root["mission"] is { } mission ? "mission " + Text(mission) : null,
            root["camera"]?["zone"] is JsonValue zone ? "zone " + zone : null));
        if (root["hovered"] is JsonObject hovered) Line("Hovered", Item(hovered));
        if (root["selected"] is JsonArray { Count: > 0 } selected)
        {
            var first = selected[0];
            string facts = Join(Text(first?["kind"]), first?["tag"] is { } tag ? "tag " + Text(tag) : null,
                first?["package"] is { } package ? "package " + Text(package) : null) ?? "";
            Line("Selected", Item(first) + (facts.Length > 0 ? " (" + facts + ")" : "")
                + (selected.Count > 1 ? $" and {selected.Count - 1} more" : ""));
            if (first?["values"] is JsonObject values && values.Count > 0)
                Line("  Values", string.Join("; ", values.Select(pair => $"{pair.Key}={Clip(Text(pair.Value), 60)}")));
        }
        if (root["camera"]?["position"] is JsonArray eye) Line("Camera", string.Join(", ", eye.Select(Text)));
        if (root["open"] is JsonObject open)
            Line("Open", string.Join("; ", open.Where(pair => pair.Value is not null).Select(pair => $"{pair.Key} {Text(pair.Value) ?? pair.Value!.ToJsonString()}")));
        else Line("Open file", Text(root["openFile"]));
        if (root["pinned"] is JsonArray { Count: > 0 } pinned) Line("Pinned", string.Join(", ", pinned.Take(8).Select(Item)));
        if (root["recent"] is JsonArray { Count: > 0 } recent)
            Line("Recent", string.Join(", ", recent.Take(5).Select(item => $"{Text(item?["kind"])} {Text(item?["label"])} ({Text(item?["secondsAgo"])}s ago)")));
        if (lines.Count == 0) return "";
        var text = new StringBuilder("Solforge live context (what the user is looking at; 'this', 'that' and 'here' mean it). " +
            "Call the solforge tools get_context, get_open_documents or get_object for more.\n");
        foreach (string line in lines)
        {
            if (text.Length + line.Length + 1 > limit) { text.Append("..."); break; }
            text.Append(line).Append('\n');
        }
        return text.ToString();
    }

    private static string? Join(params string?[] parts) =>
        parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToArray() is { Length: > 0 } kept ? string.Join(", ", kept) : null;

    private static string Clip(string? text, int length) =>
        text is null ? "" : (text = text.ReplaceLineEndings(" ")).Length > length ? text[..length] + "..." : text;
}
