// Solforge Phase 47B checks: MCP tool list and schemas over the stdio shim, find_object resolution of viewport
// labels, the attention log, project path rules and per-client config generation. No game install needed.
using System.Diagnostics;
using System.Text.Json.Nodes;
using Solforge.Mcp;

var checks = new List<string>();
string scratch = Path.Combine(Path.GetTempPath(), "solforge-mcp-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    FindObject();
    checks.Add("find_object resolves 'static mesh 7489', 'last city crater', '[182]' and tag hashes; numbers are exact; other maps never match");
    Attention();
    checks.Add("the attention log folds repeats, lists newest first, and 'the one I just hovered' wins a tie");
    ProjectPaths();
    checks.Add("project files resolve inside the project only; packages, manifest and build output are not editable");
    Configs();
    checks.Add("client configs: .mcp.json, Cline and Antigravity get the solforge stdio entry; other servers survive; rewrite is idempotent");
    await StdioRoundTrip();
    checks.Add("initialize, tools/list (16 tools with object schemas) and tools/call round trip over the solforge-mcp stdio shim");
    foreach (string check in checks) Console.WriteLine("PASS " + check);
    Console.WriteLine($"PASS checks={checks.Count}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("FAIL " + exception);
    return 1;
}
finally
{
    try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static ObjectEntry[] Fixture() =>
[
    new("w:1", "Botza District / Static mesh 7489 - Last city crater [182]", "Mesh", ObjectScope.Viewport, "Botza District", "w64_cosmodrome_0123", 0x80A1B2C3),
    new("w:2", "Botza District / Static mesh 748 - Rubble [12]", "Mesh", ObjectScope.Tree, "Botza District"),
    new("w:3", "Botza District / Static mesh 74890 - Crater rim [183]", "Mesh", ObjectScope.Tree, "Botza District"),
    new("w:4", "Botza District / Static mesh 50 - Tube [7]", "Mesh", ObjectScope.Viewport, "Botza District"),
    new("w:5", "Botza District / Static mesh 51 - Tube [8]", "Mesh", ObjectScope.Viewport, "Botza District"),
    new("w:6", "Moon / Static mesh 7489 - Last city crater [182]", "Mesh", ObjectScope.Tree, "Moon"),
    new("tag:80FFEE01", "Last city crater rock", "static model", ObjectScope.Asset, null, "w64_shared_01", 0x80FFEE01),
];

static void FindObject()
{
    var entries = Fixture();
    string Top(string query, IReadOnlyDictionary<string, float>? recency = null) =>
        ObjectIndex.Find(entries, query, "Botza District", recency) is [var first, ..] ? first.Entry.Id : "(none)";
    Expect(Top("static mesh 7489") == "w:1", "static mesh 7489 -> " + Top("static mesh 7489"));
    Expect(Top("do you see static mesh 7489?") == "w:1", "spoken question");
    Expect(Top("Static mesh 7489 - Last city crater [182]") == "w:1", "exact label");
    Expect(Top("last city crater") == "w:1", "words -> " + Top("last city crater"));
    Expect(Top("[182]") == "w:1", "[182] -> " + Top("[182]"));
    Expect(Top("0x80A1B2C3") == "w:1", "tag hash");
    Expect(Top("static mesh 748") == "w:2", "748 must not take 7489");
    Expect(ObjectIndex.Find(entries, "static mesh 7489", "Botza District").All(match => match.Entry.Id != "w:6"), "another map matched");
    Expect(ObjectIndex.Find(entries, "static mesh 7489", "Moon") is [{ Entry.Id: "w:6" }, ..], "map scoping on the Moon");
    Expect(ObjectIndex.Find(entries, "crater rock", "Botza District").Any(match => match.Entry.Scope == ObjectScope.Asset), "asset index row");
    Expect(ObjectIndex.Find(entries, "zebra", "Botza District").Count == 0, "nonsense matched");
    var recent = new Dictionary<string, float> { ["w:5"] = 1f };
    Expect(Top("that tube", recent) == "w:5", "recency breaks the tube tie");
    var json = ObjectIndex.ToJson(ObjectIndex.Find(entries, "static mesh 7489", "Botza District"));
    Expect(json[0]?["package"]?.ToString() == "w64_cosmodrome_0123" && json[0]?["tag"]?.ToString() == "0x80A1B2C3", "match JSON");
}

static void Attention()
{
    var time = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
    var log = new AttentionLog(4) { Clock = () => time };
    log.Record(AttentionKind.Hover, "w:4", "Tube 50");
    log.Record(AttentionKind.Hover, "w:4", "Tube 50");
    log.Record(AttentionKind.Select, "w:1", "Static mesh 7489");
    log.Record(AttentionKind.Hover, "w:5", "Tube 51");
    var recent = log.Recent(10);
    Expect(recent.Count == 3 && recent[0].Id == "w:5" && recent[1].Kind == AttentionKind.Select, "order and folding");
    Expect(log.Recent(10, AttentionKind.Select) is [{ Id: "w:1" }], "kind filter");
    for (int i = 0; i < 10; i++) log.Record(AttentionKind.Focus, "f" + i, "F");
    Expect(log.Recent(100).Count == 4, "capacity");
    Expect(log.ToJson(1)[0]?["kind"]?.ToString() == "focus", "json kind");
    Expect(log.Recency()["f9"] > log.Recency()["f6"], "recency weights");
}

void ProjectPaths()
{
    string project = Path.Combine(scratch, "project");
    Directory.CreateDirectory(Path.Combine(project, "scripts"));
    File.WriteAllText(Path.Combine(project, "scripts", "freeroam.lua"), "-- crater 80A1B2C3\nreturn 1\n");
    Expect(ProjectFiles.Resolve(project, [], "scripts/freeroam.lua") is not null, "resolve");
    Expect(ProjectFiles.Resolve(project, [], "../outside.txt") is null, "escape");
    Expect(ProjectFiles.EditablePath(project, "scripts/new.lua", out _) is not null, "editable");
    foreach (string refused in new[] { "../x.lua", "C:/Windows/x.lua", "bin/x.lua", "data/a.pkg", "sunrise.project.json", "sdk:api.md" })
        Expect(ProjectFiles.EditablePath(project, refused, out _) is null, "not refused: " + refused);
    var hits = ProjectFiles.Search(project, [], "80a1b2c3", ".lua", 10);
    Expect(hits.Count == 1 && hits[0]?["line"]?.GetValue<int>() == 1, "search");
    Expect(ProjectFiles.Read(Path.Combine(project, "scripts", "freeroam.lua"), "scripts/freeroam.lua", 2, 5)["text"]?.ToString()
        .StartsWith("2\treturn 1", StringComparison.Ordinal) == true, "read offset");
}

void Configs()
{
    string project = Path.Combine(scratch, "config project");
    string home = Path.Combine(scratch, "home"), appData = Path.Combine(scratch, "appdata");
    Directory.CreateDirectory(project);
    File.WriteAllText(Path.Combine(project, ".mcp.json"), "{\"mcpServers\":{\"other\":{\"command\":\"x\"}},\"keep\":1}");
    Directory.CreateDirectory(Path.Combine(appData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev"));
    string shim = @"C:\Solforge\solforge-mcp.exe";
    var written = McpClientConfigs.WriteAll(project, shim, home, appData);
    Expect(written.Select(item => item.Client).SequenceEqual(["Claude Code", "Cline", "Cline (VS Code)", "Antigravity"]), "clients");
    var claude = JsonNode.Parse(File.ReadAllText(Path.Combine(project, ".mcp.json")))!;
    Expect(claude["mcpServers"]?["other"]?["command"]?.ToString() == "x" && claude["keep"]?.GetValue<int>() == 1, "kept other entries");
    var entry = claude["mcpServers"]?["solforge"];
    Expect(entry?["command"]?.ToString() == shim && entry?["type"]?.ToString() == "stdio", "claude entry");
    Expect(entry?["args"]?.AsArray().Select(node => node?.ToString()).SequenceEqual(["--project", project]) == true, "project scope");
    foreach (var item in written.Skip(1))
        Expect(JsonNode.Parse(File.ReadAllText(item.Path))?["mcpServers"]?["solforge"]?["command"]?.ToString() == shim, item.Client);
    var stamp = File.GetLastWriteTimeUtc(Path.Combine(project, ".mcp.json"));
    McpClientConfigs.WriteAll(project, shim, home, appData);
    Expect(File.GetLastWriteTimeUtc(Path.Combine(project, ".mcp.json")) == stamp, "idempotent");
}

async Task StdioRoundTrip()
{
    string project = Path.Combine(scratch, "project");
    string discovery = Path.Combine(scratch, "discovery");
    var host = new FixtureHost(project);
    var dispatcher = new McpDispatcher(SolforgeTools.Create(host), "0.1.0", SolforgeTools.Instructions);
    await using var server = new McpHttpServer(dispatcher);
    server.Start();
    McpDiscovery.Write(new McpDiscoveryEntry(Environment.ProcessId, server.Url, server.Token, project, DateTimeOffset.UtcNow), discovery);
    Expect(McpDiscovery.Find(project, discovery)?.Url == server.Url, "discovery");

    string shim = FindShim();
    var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    start.ArgumentList.Add(shim);
    start.ArgumentList.Add("--project");
    start.ArgumentList.Add(project);
    start.Environment["SOLFORGE_MCP_DISCOVERY"] = discovery;
    using var process = Process.Start(start)!;
    async Task<JsonNode> Call(int id, string method, JsonObject? parameters = null)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        await process.StandardInput.WriteLineAsync(request.ToJsonString());
        await process.StandardInput.FlushAsync();
        string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Expect(line is not null, method + ": no reply; " + await process.StandardError.ReadToEndAsync());
        var reply = JsonNode.Parse(line!)!;
        Expect(reply["id"]?.GetValue<int>() == id && reply["error"] is null, method + ": " + line);
        return reply["result"]!;
    }
    try
    {
        var initialized = await Call(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "checks", ["version"] = "1" } });
        Expect(initialized["serverInfo"]?["name"]?.ToString() == "solforge" && initialized["capabilities"]?["tools"] is not null, "initialize");
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        var tools = (await Call(2, "tools/list"))["tools"]!.AsArray();
        string[] expected = ["get_context", "find_object", "get_object", "get_viewport_screenshot", "list_visible", "get_attention_log",
            "read_project_file", "search_project", "get_tag", "get_package_entry", "get_live_state",
            "select_object", "focus_camera", "highlight", "open_file", "apply_project_edit"];
        Expect(tools.Select(tool => tool?["name"]?.ToString()).SequenceEqual(expected), "tool names: " + string.Join(",", tools.Select(tool => tool?["name"])));
        foreach (var tool in tools)
        {
            var schema = tool!["inputSchema"]!;
            Expect(schema["type"]?.ToString() == "object" && schema["properties"] is JsonObject, "schema of " + tool["name"]);
            foreach (var required in schema["required"]?.AsArray() ?? new JsonArray())
                Expect(schema["properties"]![required!.ToString()] is not null, $"{tool["name"]} requires an undeclared {required}");
            bool readOnly = tool["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true;
            Expect(readOnly == Array.IndexOf(expected, tool["name"]!.ToString()) < 11, "read-only flag of " + tool["name"]);
        }
        var found = await Call(3, "tools/call", new JsonObject { ["name"] = "find_object", ["arguments"] = new JsonObject { ["query"] = "static mesh 7489" } });
        var text = JsonNode.Parse(found["content"]![0]!["text"]!.ToString())!;
        Expect(text["matches"]?[0]?["id"]?.ToString() == "w:1", "find_object over stdio: " + text.ToJsonString());
        var denied = await Call(4, "tools/call", new JsonObject { ["name"] = "select_object", ["arguments"] = new JsonObject { ["id"] = "w:1" } });
        Expect(denied["isError"]?.GetValue<bool>() == true && host.Approvals == 1, "approval gate");
        var unknown = await Call(5, "ping");
        Expect(unknown is JsonObject, "ping");
    }
    finally
    {
        process.StandardInput.Close();
        if (!process.WaitForExit(5000)) process.Kill();
    }
    // Wrong token is refused.
    using var http = new HttpClient();
    var wrong = await http.PostAsync(server.Url, new StringContent("{}"));
    Expect((int)wrong.StatusCode == 401, "token check");
}

static string FindShim()
{
    for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
    {
        string bin = Path.Combine(folder.FullName, "src", "Solforge.Mcp.Shim", "bin");
        if (!Directory.Exists(bin)) continue;
        return Directory.EnumerateFiles(bin, "solforge-mcp.dll", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Contains("Release", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(path => File.GetLastWriteTimeUtc(path)).FirstOrDefault()
            ?? throw new FileNotFoundException("Build src/Solforge.Mcp.Shim first.");
    }
    throw new DirectoryNotFoundException("Repository root not found above " + AppContext.BaseDirectory);
}

/** A fixed scene: the find_object fixture, no viewport, and an approver that always declines. */
sealed class FixtureHost(string project) : ISolforgeHost
{
    public int Approvals;
    public bool Paused => false;
    public string? ProjectRoot => project;
    public IReadOnlyList<string> SdkRoots => [];
    public string? CurrentMap => "Botza District";
    public AttentionLog Attention { get; } = new();
    public string ContextJson => "{\"map\":\"Botza District\"}";
    public IReadOnlyList<VisibleObject> Visible => [];
    public Task<IReadOnlyList<ObjectEntry>> ObjectsAsync(string query, CancellationToken cancel) =>
        Task.FromResult<IReadOnlyList<ObjectEntry>>(
        [
            new("w:1", "Botza District / Static mesh 7489 - Last city crater [182]", "Mesh", ObjectScope.Viewport, "Botza District"),
            new("w:3", "Botza District / Static mesh 74890 - Crater rim [183]", "Mesh", ObjectScope.Tree, "Botza District")
        ]);
    public Task<JsonObject?> GetObjectAsync(string id, CancellationToken cancel) => Task.FromResult<JsonObject?>(null);
    public Task<byte[]?> ScreenshotAsync(string? objectId, bool labels, CancellationToken cancel) => Task.FromResult<byte[]?>(null);
    public Task<JsonObject?> GetTagAsync(uint hash, CancellationToken cancel) => Task.FromResult<JsonObject?>(null);
    public Task<JsonObject?> GetPackageEntryAsync(string id, CancellationToken cancel) => Task.FromResult<JsonObject?>(null);
    public JsonObject LiveState() => new() { ["attached"] = false };
    public Task<bool> ApproveAsync(string tool, string summary, CancellationToken cancel) { Interlocked.Increment(ref Approvals); return Task.FromResult(false); }
    public Task<string> RunActionAsync(string tool, JsonObject args, CancellationToken cancel) => Task.FromResult("ran");
}
