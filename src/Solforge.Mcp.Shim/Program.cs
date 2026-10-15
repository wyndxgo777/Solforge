// solforge-mcp: a stdio MCP server for clients that only speak stdio. Each line on stdin is one JSON-RPC
// message; it is POSTed to the Solforge that has the project open (found through the discovery file in
// %LOCALAPPDATA%\Solforge\mcp) and the reply is written to stdout as one line. Logs go to stderr.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Solforge.Mcp;

string? project = null;
for (int i = 0; i < args.Length; i++)
    if (args[i] == "--project" && i + 1 < args.Length) project = args[++i];
    else if (args[i] is "--help" or "-h")
    {
        Console.Error.WriteLine("solforge-mcp [--project <folder>]  forwards MCP stdio to a running Solforge.");
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

async Task Reply(string? id, string? raw, string? error)
{
    // Notifications get no reply, even on failure.
    if (raw is null && id is null) { Console.Error.WriteLine("solforge-mcp: " + error); return; }
    raw ??= $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":-32000,\"message\":\"{JsonEncodedText.Encode(error ?? "")}\"}}}}";
    await writeGate.WaitAsync();
    try { await stdout.WriteLineAsync(raw); }
    finally { writeGate.Release(); }
}
