// solforge-mcp: a stdio MCP server for clients that only speak stdio. Each line on stdin is one JSON-RPC
// message; it is POSTed to the Solforge that has the project open (found through the discovery file in
// %LOCALAPPDATA%\Solforge\mcp) and the reply is written to stdout as one line. Logs go to stderr.
// With --context it prints the live context as a few plain lines and exits (the Claude Code prompt hook).
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solforge.Mcp;

string? project = null;
bool context = false, gemini = false, clinePass = false;
for (int i = 0; i < args.Length; i++)
    if (args[i] == "--project" && i + 1 < args.Length) project = args[++i];
    else if (args[i] == "--context") context = true;
    else if (args[i] == "--gemini") gemini = true;
    else if (args[i] == "--clinepass") clinePass = true;
    else if (args[i] is "--help" or "-h")
    {
        Console.Error.WriteLine("solforge-mcp [--project <folder>] [--context] [--gemini] [--clinepass]  forwards MCP stdio to a running Solforge;");
        Console.Error.WriteLine("  --context prints its live context as plain text and exits.");
        Console.Error.WriteLine("  --gemini serves the ask_gemini tool instead (runs the Antigravity CLI, agy -p, on your own agy login).");
        Console.Error.WriteLine("  --clinepass serves the ask_clinepass tool instead (runs the Cline CLI, cline -P cline-pass, on your own Cline login).");
        return 0;
    }
if (context) return await PrintContext(project);
if (gemini || clinePass)
{
    // Works without Solforge running, so any Claude Code session can add it (claude mcp add --scope user).
    var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
    await (gemini ? AgyDelegate.Serve(reader, output) : ClineDelegate.Serve(reader, output));
    return 0;
}

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
var writeGate = new SemaphoreSlim(1, 1);
string? session = null;
Task initialized = Task.CompletedTask;
var pending = new List<Task>();
using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
while (await input.ReadLineAsync() is { } line)
{
    if (line.Length == 0) continue;
    bool initialize = line.Contains("\"initialize\"", StringComparison.Ordinal);
    var previous = initialized;
    var work = Task.Run(async () =>
    {
        if (!initialize) await previous;
        await Forward(line);
    });
    if (initialize) initialized = work;
    pending.Add(work);
    pending.RemoveAll(task => task.IsCompleted);
}
await Task.WhenAll(pending);
return 0;

/** Sends one stdin line to the editor MCP endpoint and writes the reply to stdout. */
async Task Forward(string message)
{
    string? id = null;
    try
    {
        using var document = JsonDocument.Parse(message);
        if (document.RootElement.TryGetProperty("id", out var idElement)) id = idElement.GetRawText();
    }
    catch (JsonException) { }
    var server = McpDiscovery.Find(project);
    if (server is null)
    {
        await Reply(id, null, "Solforge is not running with this project open. Start Solforge, open the project and the AI workspace.");
        return;
    }
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Url)
        { Content = new StringContent(message, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        // Several agents can share one Solforge: the pid tells their sessions apart in get_sessions and the presence list.
        request.Headers.TryAddWithoutValidation("X-Solforge-Client-Pid" /* McpSessionRegistry.PidHeader */, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await http.SendAsync(request);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values)) session = values.FirstOrDefault() ?? session;
        if (response.StatusCode == HttpStatusCode.Accepted) return;
        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) { await Reply(id, null, $"Solforge answered {(int)response.StatusCode}: {body}"); return; }
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            foreach (string eventLine in body.Split('\n'))
                if (eventLine.StartsWith("data:", StringComparison.Ordinal)) await Reply(null, eventLine[5..].Trim(), null);
        }
        else if (body.Length > 0) await Reply(null, body.ReplaceLineEndings(" "), null);
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        await Reply(id, null, "Could not reach Solforge: " + exception.Message);
    }
}

/** Writes the reply line (or a JSON-RPC error); notifications get nothing. */
async Task Reply(string? id, string? raw, string? error)
{
    // Notifications get no reply, even on failure.
    if (raw is null && id is null) { Console.Error.WriteLine("solforge-mcp: " + error); return; }
    raw ??= $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32000,\"message\":\"{JsonEncodedText.Encode(error ?? "")}\"}}}}";
    await writeGate.WaitAsync();
    try { await stdout.WriteLineAsync(raw); }
    finally { writeGate.Release(); }
}

/** No Solforge, paused context or any failure prints nothing: the hook must never block a prompt. */
static async Task<int> PrintContext(string? project)
{
    try
    {
        if (McpDiscovery.Find(project) is not { } server) return 0;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Url)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"get_context\",\"arguments\":{}}}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return 0;
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync())?["result"];
        if (result?["isError"]?.GetValueKind() == JsonValueKind.True || result?["content"]?[0]?["text"]?.GetValue<string>() is not { } text) return 0;
        string summary = ContextSummary.Format(JsonNode.Parse(text));
        if (summary.Length == 0) return 0;
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        await stdout.WriteAsync(summary);
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException
        or InvalidOperationException or IOException) { }
    return 0;
}
