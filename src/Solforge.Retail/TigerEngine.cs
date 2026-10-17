/**
 * File: Client for the tiger-mcp web API (POST /api/tools/<tool> with JSON arguments; replies
 * {"success": bool, "result": ..., "error": "..."}). The engine is optional: every call returns a TigerReply that says
 * the engine is down instead of throwing, so the offline readers keep working.
 */
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Retail;

/** One engine reply: Ok with Result, or Error (Offline is true when the engine could not be reached at all). */
public sealed record TigerReply(bool Ok, JsonNode? Result, string? Error, bool Offline)
{
    public static TigerReply Down(string message) => new(false, null, message, true);
}

/**
 * The tiger-mcp tools Solforge uses: get_server_status, get_tag_info, get_tag_references (incoming), traverse_tag,
 * scan_tag_for_hashes (FNV names in a tag), search_localized_strings, search_strings, search_named_tags,
 * search_wordlist_hashes, find_tags_for_hash. Read-only; Solforge never starts, stops or reconfigures the engine.
 */
public sealed class TigerEngine : IDisposable
{
    /** Engine tools a caller may pass through (retail_engine). Anything that writes files is left out. */
    public static readonly string[] AllowedTools =
    [
        "get_server_status", "get_tag_info", "get_tag_details", "get_tag_references", "traverse_tag", "scan_tag_for_hashes",
        "search_localized_strings", "search_strings", "search_named_tags", "search_wordlist_hashes", "search_raw_strings",
        "find_tags_for_hash", "search_tags_by_type", "list_package_files", "read_tag_data"
    ];

    private readonly HttpClient http;
    private DateTime downUntil = DateTime.MinValue;

    public string Url { get; }

    public TigerEngine(string url, double timeoutSeconds)
    {
        Url = url.TrimEnd('/');
        http = new HttpClient { BaseAddress = new Uri(Url + "/"), Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)) };
    }

    /** Calls one engine tool. After a connection failure further calls fail fast for 15 seconds. */
    public async Task<TigerReply> CallAsync(string tool, JsonObject arguments, CancellationToken cancel)
    {
        if (!AllowedTools.Contains(tool)) return new TigerReply(false, null, $"Engine tool '{tool}' is not allowed.", false);
        if (DateTime.UtcNow < downUntil) return TigerReply.Down($"tiger-mcp at {Url} did not answer a moment ago; retry shortly.");
        try
        {
            using var response = await http.PostAsJsonAsync("api/tools/" + tool, arguments, cancel).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            JsonNode? node;
            try { node = JsonNode.Parse(body); }
            catch (JsonException) { return new TigerReply(false, null, $"HTTP {(int)response.StatusCode}: {Clip(body)}", false); }
            if (node is not JsonObject reply) return new TigerReply(false, null, "Unexpected engine reply.", false);
            bool ok = reply["success"] is JsonValue flag && flag.TryGetValue(out bool success) && success;
            return ok ? new TigerReply(true, reply["result"]?.DeepClone(), null, false)
                : new TigerReply(false, null, reply["error"]?.ToString() ?? $"HTTP {(int)response.StatusCode}", false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            downUntil = DateTime.UtcNow.AddSeconds(15);
            return TigerReply.Down($"tiger-mcp is not reachable at {Url} ({error.Message}). Offline reads (index, payloads, decoders) still work.");
        }
    }

    /** get_server_status. */
    public Task<TigerReply> StatusAsync(CancellationToken cancel) => CallAsync("get_server_status", new JsonObject(), cancel);

    /** Incoming references of a tag (get_tag_references). */
    public Task<TigerReply> ReferencesAsync(uint tag, int limit, CancellationToken cancel) =>
        CallAsync("get_tag_references", new JsonObject { ["tag"] = tag.ToString("X8"), ["limit"] = limit }, cancel);

    /** traverse_tag in mode children or ancestors. */
    public Task<TigerReply> TraverseAsync(uint tag, string mode, int depth, CancellationToken cancel) =>
        CallAsync("traverse_tag", new JsonObject { ["tag"] = tag.ToString("X8"), ["mode"] = mode, ["max_depth"] = depth }, cancel);

    /** FNV hashes inside a tag with the strings the engine knows for them (scan_tag_for_hashes). */
    public Task<TigerReply> ScanHashesAsync(uint tag, CancellationToken cancel) =>
        CallAsync("scan_tag_for_hashes", new JsonObject { ["tag"] = tag.ToString("X8") }, cancel);

    /** Localized string search by text or by exact hash. */
    public Task<TigerReply> LocalizedAsync(string? query, uint? hash, int limit, CancellationToken cancel)
    {
        var args = new JsonObject { ["limit"] = limit };
        if (query is { Length: > 0 }) args["query"] = query;
        if (hash is { } value) args["hash"] = value.ToString("x8");
        return CallAsync("search_localized_strings", args, cancel);
    }

    private static string Clip(string text) => text.Length <= 300 ? text : text[..300] + "...";

    public void Dispose() => http.Dispose();
}
