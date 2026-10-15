// Solforge Phase 47B checks: MCP tool list and schemas over the stdio shim, find_object resolution of viewport
// labels, the attention log, project path rules, per-client config generation, engine command resolution, the
// Claude Code launch line, the shim's --context hook output, get_open_documents and the auto-allow prompt detector. No game install needed.
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    checks.Add("client configs: .mcp.json, Cline, Antigravity desktop and CLI get the solforge stdio entry; an empty agy file merges without .bak; other servers survive; rewrite is idempotent");
    CommandResolution();
    checks.Add("engine commands resolve on PATH + PATHEXT like cmd: .cmd beats npm's bare script, folders with spaces, missing is null");
    ClaudeLaunch();
    checks.Add("Claude launch: --mcp-config, --settings hook, --append-system-prompt and --add-dir survive argv parsing with spaced paths; agy gets the same --add-dir folders");
    ResumeSessions();
    checks.Add("resume: claude --continue only when ~/.claude/projects/<folder> has a session; agy --conversation and cline --id pick the newest session for the same folder; none means a fresh start");
    ContextText();
    checks.Add("context summary: compact lines under 2 KB with map, hovered, selected values, open documents; nothing when paused");
    await StdioRoundTrip();
    checks.Add("initialize, tools/list (17 tools with object schemas), tools/call and get_open_documents round trip over the stdio shim");
    await ContextHook();
    checks.Add("solforge-mcp --context prints the live context, and nothing (exit 0, fast) when paused or no Solforge runs");
    Approvals();
    checks.Add("auto-allow: Claude Code, agy and Cline allow prompts match only with a one-time Yes/Allow highlighted at the bottom; No, scrollback, quotes, persistent rows and free text never do");
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
    // The agy installer leaves its config empty: it must merge as {} with no .bak.
    string agy = Path.Combine(home, ".gemini", "config", "mcp_config.json");
    Directory.CreateDirectory(Path.GetDirectoryName(agy)!);
    File.WriteAllText(agy, "");
    string shim = @"C:\Solforge\solforge-mcp.exe";
    var written = McpClientConfigs.WriteAll(project, shim, home, appData);
    Expect(written.Select(item => item.Client).SequenceEqual(
        ["Claude Code", "Cline", "Cline (VS Code)", "Antigravity (desktop)", "Antigravity CLI"]), "clients");
    Expect(written[^1].Path == agy && !File.Exists(agy + ".bak"), "agy file without .bak");
    var agyEntry = new JsonObject { ["args"] = new JsonArray("--project", project), ["command"] = shim, ["disabled"] = false };
    Expect(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(agy))?["mcpServers"]?["solforge"], agyEntry), "agy entry");
    var claude = JsonNode.Parse(File.ReadAllText(Path.Combine(project, ".mcp.json")))!;
    Expect(claude["mcpServers"]?["other"]?["command"]?.ToString() == "x" && claude["keep"]?.GetValue<int>() == 1, "kept other entries");
    var entry = claude["mcpServers"]?["solforge"];
    Expect(entry?["command"]?.ToString() == shim && entry?["type"]?.ToString() == "stdio", "claude entry");
    Expect(entry?["args"]?.AsArray().Select(node => node?.ToString()).SequenceEqual(["--project", project]) == true, "project scope");
    foreach (var item in written.Skip(1))
        Expect(JsonNode.Parse(File.ReadAllText(item.Path))?["mcpServers"]?["solforge"]?["command"]?.ToString() == shim, item.Client);
    var stamps = written.Select(item => File.GetLastWriteTimeUtc(item.Path)).ToArray();
    McpClientConfigs.WriteAll(project, shim, home, appData);
    Expect(written.Select(item => File.GetLastWriteTimeUtc(item.Path)).SequenceEqual(stamps), "idempotent");
    // Whitespace counts as empty; a damaged file is kept as .bak and replaced.
    var solforge = new JsonObject { ["command"] = shim };
    string blank = Path.Combine(scratch, "blank.json"), damaged = Path.Combine(scratch, "damaged.json");
    File.WriteAllText(blank, "  \n");
    McpClientConfigs.Merge(blank, solforge.DeepClone().AsObject());
    Expect(!File.Exists(blank + ".bak") && JsonNode.Parse(File.ReadAllText(blank))?["mcpServers"]?["solforge"] is not null, "blank merges");
    File.WriteAllText(damaged, "{not json");
    McpClientConfigs.Merge(damaged, solforge.DeepClone().AsObject());
    Expect(File.Exists(damaged + ".bak") && File.ReadAllText(damaged + ".bak") == "{not json"
        && JsonNode.Parse(File.ReadAllText(damaged))?["mcpServers"]?["solforge"] is not null, "damaged kept as .bak");
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
        string? line;
        try { line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) { line = null; }
        if (line is null)
        {
            // Stderr only ends once the shim exits, so read it only after stopping the shim.
            process.Kill();
            Expect(false, method + ": no reply; " + await process.StandardError.ReadToEndAsync());
        }
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
        string[] expected = ["get_context", "get_open_documents", "find_object", "get_object", "get_viewport_screenshot", "list_visible", "get_attention_log",
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
            Expect(readOnly == Array.IndexOf(expected, tool["name"]!.ToString()) < 12, "read-only flag of " + tool["name"]);
        }
        var found = await Call(3, "tools/call", new JsonObject { ["name"] = "find_object", ["arguments"] = new JsonObject { ["query"] = "static mesh 7489" } });
        var text = JsonNode.Parse(found["content"]![0]!["text"]!.ToString())!;
        Expect(text["matches"]?[0]?["id"]?.ToString() == "w:1", "find_object over stdio: " + text.ToJsonString());
        var denied = await Call(4, "tools/call", new JsonObject { ["name"] = "select_object", ["arguments"] = new JsonObject { ["id"] = "w:1" } });
        Expect(denied["isError"]?.GetValue<bool>() == true && host.Approvals == 1, "approval gate");
        var unknown = await Call(5, "ping");
        Expect(unknown is JsonObject, "ping");
        var documents = await Call(6, "tools/call", new JsonObject { ["name"] = "get_open_documents", ["arguments"] = new JsonObject() });
        var open = JsonNode.Parse(documents["content"]![0]!["text"]!.ToString())!;
        Expect(open["asset"]?["tag"]?.ToString() == "0x80A1B2C3" && open["script"]?["path"]?.ToString() == "scripts/freeroam.lua"
            && open["changes"]?["count"]?.GetValue<int>() == 3, "get_open_documents: " + open.ToJsonString());
        host.Paused = true;
        var paused = await Call(7, "tools/call", new JsonObject { ["name"] = "get_open_documents", ["arguments"] = new JsonObject() });
        Expect(paused["isError"]?.GetValue<bool>() == true, "get_open_documents while paused");
        host.Paused = false;
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

void CommandResolution()
{
    string bin = Path.Combine(scratch, "fake bin"), other = Path.Combine(scratch, "other");
    Directory.CreateDirectory(bin);
    Directory.CreateDirectory(other);
    File.WriteAllText(Path.Combine(bin, "fakeengine"), "#!/bin/sh\n");
    File.WriteAllText(Path.Combine(bin, "fakeengine.cmd"), "@echo off\r\n");
    File.WriteAllText(Path.Combine(other, "fakeengine.exe"), "");
    string path = $"{Path.Combine(scratch, "missing")};\"{bin}\";{other}";
    Expect(EngineCommands.Resolve("fakeengine", path, ".COM;.EXE;.BAT;.CMD") == Path.Combine(bin, "fakeengine.cmd"), "PATH order, then PATHEXT");
    Expect(EngineCommands.Resolve("fakeengine", path, ".EXE") == Path.Combine(other, "fakeengine.exe"), "PATHEXT filter");
    Expect(EngineCommands.Resolve("fakeengine.cmd", path) == Path.Combine(bin, "fakeengine.cmd"), "name with extension");
    Expect(EngineCommands.Resolve(Path.Combine(other, "fakeengine"), "") == Path.Combine(other, "fakeengine.exe"), "explicit folder");
    Expect(EngineCommands.Resolve("agy", path) is null && EngineCommands.Resolve(" ", path) is null, "missing engine");
    Expect(EngineCommands.Join([$"{bin};{other}", $"{other}\\;{bin}", ""]) == $"{bin};{other}", "PATH join keeps the first of each folder");
}

void ClaudeLaunch()
{
    string top = Path.Combine(scratch, "launch root");
    string project = Path.Combine(top, "10-Project Sol", "Project Sol");
    string game = Path.Combine(scratch, "Sunrise Game");
    Directory.CreateDirectory(Path.Combine(project, "scripts"));
    Directory.CreateDirectory(game);
    File.WriteAllText(Path.Combine(top, "10-Project Sol", "CLAUDE.md"), "rules");
    string shim = Path.Combine(scratch, "Solforge App", "solforge-mcp.exe");
    var files = EngineLaunch.WriteClaudeFiles(project, shim, Path.Combine(scratch, "mcp"));
    var config = JsonNode.Parse(File.ReadAllText(files.McpConfig))!["mcpServers"]?["solforge"];
    Expect(config?["command"]?.ToString() == shim && config?["type"]?.ToString() == "stdio"
        && config?["args"]?.AsArray().Select(node => node?.ToString()).SequenceEqual(["--project", project]) == true, "mcp-config entry");
    string? hook = JsonNode.Parse(File.ReadAllText(files.Settings))?["hooks"]?["UserPromptSubmit"]?[0]?["hooks"]?[0]?["command"]?.ToString();
    Expect(hook == $"\"{shim.Replace('\\', '/')}\" --context --project \"{project.Replace('\\', '/')}\"", "prompt hook: " + hook);
    // The scratch folder stands in for the user profile: ancestors stop below it.
    var folders = EngineLaunch.AddDirs(project, [game, Path.Combine(project, "scripts"), Path.Combine(scratch, "gone"), null, game + "\\"], scratch);
    string ancestor = Path.Combine(top, "10-Project Sol");
    Expect(folders.SequenceEqual([game, ancestor]), "add-dirs: " + string.Join(" | ", folders));
    Directory.CreateDirectory(Path.Combine(top, ".claude"));
    Expect(EngineLaunch.AddDirs(project, [game], scratch).SequenceEqual([game, top]), "a .claude ancestor covers the nearer one");
    string arguments = EngineLaunch.ClaudeArguments(files, folders);
    string[] argv = Argv("claude.exe " + arguments);
    Expect(argv.Skip(1).SequenceEqual(["--mcp-config", files.McpConfig, "--settings", files.Settings, "--append-system-prompt",
        EngineLaunch.SystemPrompt, "--add-dir", game, "--add-dir", ancestor]), "argv: " + string.Join(" | ", argv));
    Expect(!EngineLaunch.SystemPrompt.Contains('"') && !EngineLaunch.SystemPrompt.Contains('%'), "prompt is safe through cmd");
    string line = EngineLaunch.CmdLine(@"C:\Program Files\nodejs\claude.cmd", arguments);
    Expect(line.StartsWith("cmd.exe /d /s /k \"\"C:\\Program Files\\nodejs\\claude.cmd\" --mcp-config ", StringComparison.Ordinal)
        && line.EndsWith(ancestor + "\"\"", StringComparison.Ordinal), "cmd line: " + line);
    Expect(EngineLaunch.Quote(@"C:\a b\") == "\"C:\\a b\\\\\"" && EngineLaunch.Quote("plain") == "plain", "quoting");
    Expect(EngineLaunch.ClaudeArguments(null, []).StartsWith("--append-system-prompt", StringComparison.Ordinal), "no config files");
    // agy gets the same folders, quoted: both scratch paths hold spaces.
    string agy = EngineLaunch.AgyArguments(folders);
    Expect(agy == $"--add-dir \"{game}\" --add-dir \"{ancestor}\"", "agy arguments: " + agy);
    Expect(Argv("agy.exe " + agy).Skip(1).SequenceEqual(["--add-dir", game, "--add-dir", ancestor]), "agy argv");
    Expect(EngineLaunch.AgyArguments([]).Length == 0, "agy without folders");
}

void ResumeSessions()
{
    string home = Path.Combine(scratch, "resume home");
    string folder = @"C:\Users\Seth\Desktop\sol\09-solforge\publish", other = @"C:\Other";
    string dir = EngineLaunch.ClaudeSessionsDir(folder + "\\", home);
    Expect(Path.GetFileName(dir) == "C--Users-Seth-Desktop-sol-09-solforge-publish", "claude sessions dir: " + dir);
    Expect(EngineLaunch.ClaudeSession(folder, home) is null, "no claude session on a first run");
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "old.jsonl"), "{}");
    File.SetLastWriteTimeUtc(Path.Combine(dir, "old.jsonl"), DateTime.UtcNow.AddHours(-1));
    File.WriteAllText(Path.Combine(dir, "6f24feaa.jsonl"), "{}");
    Expect(EngineLaunch.ClaudeSession(folder, home) == "6f24feaa", "newest claude session");
    Expect(EngineLaunch.ResumeArguments("claude", "6f24feaa") == "--continue", "claude resumes with --continue");
    Expect(EngineLaunch.ResumeArguments("claude", null) == "" && EngineLaunch.ResumeArguments("cline", "") == "", "no session, no flag");
    Expect(EngineLaunch.JoinArguments("--add-dir x", "", "--continue") == "--add-dir x --continue"
        && EngineLaunch.JoinArguments("", "--continue") == "--continue", "join");
    string[] agyLines =
    [
        new JsonObject { ["display"] = "a", ["workspace"] = folder, ["conversationId"] = "first" }.ToJsonString(),
        new JsonObject { ["display"] = "/mcp", ["workspace"] = folder, ["type"] = "slash_command" }.ToJsonString(),
        "not json",
        new JsonObject { ["display"] = "b", ["workspace"] = folder.ToLowerInvariant() + "\\", ["conversationId"] = "second" }.ToJsonString(),
        new JsonObject { ["display"] = "c", ["workspace"] = other, ["conversationId"] = "elsewhere" }.ToJsonString()
    ];
    Expect(EngineLaunch.AgySession(folder, agyLines) == "second", "agy: last conversation for this workspace");
    Expect(EngineLaunch.AgySession(@"C:\Nowhere", agyLines) is null, "agy: none for another folder");
    Expect(EngineLaunch.AgySession(folder, home) is null, "agy: no history file");
    Expect(EngineLaunch.ResumeArguments("agy", "second") == "--conversation second", "agy resume flag");
    string cline = "loading\n" + new JsonArray(
        new JsonObject { ["sessionId"] = "old", ["cwd"] = folder, ["startedAt"] = "2026-10-14T10:00:00.000Z" },
        new JsonObject { ["sessionId"] = "sub", ["cwd"] = folder, ["startedAt"] = "2026-10-15T12:00:00.000Z", ["isSubagent"] = true },
        new JsonObject { ["sessionId"] = "new", ["cwd"] = "x", ["workspaceRoot"] = folder, ["startedAt"] = "2026-10-15T11:00:00.000Z" },
        new JsonObject { ["sessionId"] = "away", ["cwd"] = other, ["startedAt"] = "2026-10-15T13:00:00.000Z" }).ToJsonString();
    Expect(EngineLaunch.ClineSession(folder, cline) == "new", "cline: newest top-level session for this folder");
    Expect(EngineLaunch.ClineSession(@"C:\Nowhere", cline) is null && EngineLaunch.ClineSession(folder, "error: no history") is null, "cline: none");
    Expect(EngineLaunch.ResumeArguments("cline", "1792083166619_vt4lw") == "--id 1792083166619_vt4lw", "cline resume flag");
}

static void ContextText()
{
    var context = JsonNode.Parse(FixtureHost.Context)!;
    string text = ContextSummary.Format(context);
    Expect(text.Length is > 0 and <= ContextSummary.Limit, "length " + text.Length);
    foreach (string part in new[] { "Map: Botza District", "Hovered: Botza District / Static mesh 7489", "tag 0x80A1B2C3",
        "Values: Name=Last city crater", "script scripts/freeroam.lua", "Recent: select" })
        Expect(text.Contains(part, StringComparison.Ordinal), $"missing '{part}' in:\n{text}");
    Expect(ContextSummary.Format(JsonNode.Parse("{\"paused\":true}")) == "" && ContextSummary.Format(null) == "", "paused prints nothing");
    var big = context.DeepClone().AsObject();
    var pinned = new JsonArray();
    for (int i = 0; i < 400; i++) pinned.Add(new JsonObject { ["id"] = "w:" + i, ["label"] = new string('x', 400) });
    big["pinned"] = pinned;
    big["recent"] = null;
    Expect(ContextSummary.Format(big).Length <= ContextSummary.Limit + 3, "capped");
}

async Task ContextHook()
{
    string project = Path.Combine(scratch, "project");
    string discovery = Path.Combine(scratch, "hook discovery");
    var host = new FixtureHost(project);
    var dispatcher = new McpDispatcher(SolforgeTools.Create(host), "0.1.0", SolforgeTools.Instructions);
    await using var server = new McpHttpServer(dispatcher);
    server.Start();
    McpDiscovery.Write(new McpDiscoveryEntry(Environment.ProcessId, server.Url, server.Token, project, DateTimeOffset.UtcNow), discovery);
    async Task<(int Code, string Output, TimeSpan Time)> Run(string folder)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { FindShim(), "--context", "--project", project }) start.ArgumentList.Add(arg);
        start.Environment["SOLFORGE_MCP_DISCOVERY"] = folder;
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(20000)) { process.Kill(); Expect(false, "--context hung"); }
        return (process.ExitCode, await output, clock.Elapsed);
    }
    var live = await Run(discovery);
    Expect(live.Code == 0 && live.Output.Contains("Map: Botza District", StringComparison.Ordinal)
        && live.Output.Contains("Selected: Botza District / Static mesh 7489", StringComparison.Ordinal)
        && live.Output.Length <= ContextSummary.Limit + 3, "--context output: " + live.Output);
    host.Paused = true;
    var paused = await Run(discovery);
    Expect(paused.Code == 0 && paused.Output.Length == 0, "--context while paused: " + paused.Output);
    string empty = Path.Combine(scratch, "no solforge");
    Directory.CreateDirectory(empty);
    var none = await Run(empty);
    Expect(none.Code == 0 && none.Output.Length == 0 && none.Time < TimeSpan.FromSeconds(10), $"--context without Solforge: {none.Output} in {none.Time}");
}

static string[] Argv(string commandLine)
{
    IntPtr list = NativeArgs.CommandLineToArgvW(commandLine, out int count);
    try
    {
        var argv = new string[count];
        for (int i = 0; i < count; i++) argv[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(list, i * IntPtr.Size))!;
        return argv;
    }
    finally { NativeArgs.LocalFree(list); }
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

static void Approvals()
{
    string[] screen(params string[] bottom) => [.. Enumerable.Repeat("", 6), .. bottom];
    const string Nav = "   ↑/↓ Navigate · tab Amend", Esc = "  esc to cancel                                   accept-edits · Gemini 3.8 Flash · high";
    string[] agy(int pointed = 1, string tool = "solforge/get_viewport_screenshot")
    {
        string[] options =
        [
            "Yes, allow tool call", $"Yes, and always allow tool '{tool}' in this conversation", $"Yes, and always allow tool '{tool}' (Persist to settings.json)",
            "No, deny tool call", $"No, and always deny tool '{tool}' in this conversation", $"No, and always deny tool '{tool}' (Persist to settings.json)"
        ];
        return screen([
            "  " + tool, "  Allow calling this tool?",
            .. options.Select((label, i) => (i + 1 == pointed ? "> " : "  ") + $"{i + 1}. {label}"),
            Nav, Esc]);
    }
    var first = ApprovalPrompts.Find("agy", agy());
    Expect(first is { Choice: "Yes, allow tool call", Question: "Allow calling this tool?" }, "agy tool call");
    Expect(ApprovalPrompts.Find("agy", agy())?.Signature == first!.Signature, "agy: same prompt, same signature");
    Expect(ApprovalPrompts.Find("agy", agy(tool: "solforge/find_object"))?.Signature is { } next && next != first.Signature, "agy: next prompt differs");
    Expect(ApprovalPrompts.Find("agy", agy(4)) is null, "agy: No highlighted");
    Expect(ApprovalPrompts.Find("agy", agy(2)) is null, "agy: always-allow highlighted");
    Expect(ApprovalPrompts.Find("claude", agy()) is null && ApprovalPrompts.Find("custom", agy()) is null, "other engines' layouts");
    Expect(ApprovalPrompts.Find("agy", screen("Run this command?", "> 1. Yes, run command", "  2. Yes, and always allow in this conversation for commands",
        "     that start with 'dotnet'", "  3. Yes, and always allow for commands that start with 'dotnet' (Persist to settings.json)", "  4. No, cancel", Nav, Esc))?.Choice
        == "Yes, run command", "agy command, wrapped option");
    // Seth's screen: the terminal hard-wraps option 2 back to column 0, and the footer carries ctrl+g.
    Expect(ApprovalPrompts.Find("agy", screen("Requesting permission for:", "   dotnet run", "", "Run this command?", "> 1. Yes, run command",
        "  2. Yes, and always allow in this conversation for commands that start with 'dotnet", "run'",
        "  3. Yes, and always allow for commands that start with 'dotnet run' (Persist to", "settings.json)", "  4. No, cancel", "",
        "  ↑/↓ Navigate · tab Amend · ctrl+g edit/expand command", "esc to cancel                         accept-edits · Gemini 3.8 Flash · high"))?.Choice
        == "Yes, run command", "agy command, hard-wrapped options and ctrl+g footer");
    foreach (var (asked, allow, decline) in new[]
    {
        ("Allow access to this URL?", "Yes, allow access", "No, deny access"), ("Allow access to this file?", "Yes, allow access", "No, deny access"),
        ("Allow creation of this file?", "Yes, allow creation", "No, deny creation"), ("Grant requested permission?", "Yes, grant permission", "No, deny permission"),
        ("Do you want to proceed?", "Yes, proceed", "No, cancel")
    })
        Expect(ApprovalPrompts.Find("agy", screen("Requesting permission for:", "  target", asked, "> 1. " + allow, "  2. " + decline, Nav, Esc)) is { } found
            && found.Question == asked && found.Choice == allow, "agy " + asked);
    Expect(ApprovalPrompts.Find("agy", screen("Accept this file edit?", "> 1. Yes, accept this change", "  2. No, reject this change",
        "Esc disabled during file edits — press 1 to accept or 2 to reject.")) is { Choice: "Yes, accept this change" }, "agy file edit");
    Expect(ApprovalPrompts.Find("agy", screen("Allow administrator elevation?", "> 1. Yes, elevate", "  2. No, deny", Esc)) is null, "agy elevation stays manual");
    Expect(ApprovalPrompts.Find("agy", screen("Run this command?", "  dotnet test", " 🔓 Allow sandbox bypass for command execution?",
        "⚠️ Confirm the command is safe to run outside of the sandbox with full network and disk access.", "> 1. Yes, run command", "  2. No, cancel", Nav, Esc)) is null,
        "agy sandbox bypass under a stale question");
    Expect(ApprovalPrompts.Find("agy", screen("Do you trust the contents of this project?", "> 1. Yes, I trust this folder", "  2. No, exit", "enter Confirm · esc to cancel")) is null, "agy trust");
    Expect(ApprovalPrompts.Find("agy", screen("Question 1/1:", "Do you want to proceed?", "> 1. Yes, proceed", "  2. No, cancel", "  3. Write-in", Nav)) is null, "agy model question");
    Expect(ApprovalPrompts.Find("agy", screen("Allow calling this tool?", "> 1. Yes, allow tool call", "  2. No, deny tool call", Esc, "", "> ")) is null, "agy: options not at the bottom");
    Expect(ApprovalPrompts.Find("agy", screen("Allow calling this tool?", Nav, Esc)) is null, "agy: question without options");
    Expect(ApprovalPrompts.Find("agy", [.. agy(), "", "Done, the screenshot is attached.", "> "]) is null, "agy: prompt in scrollback, output below");
    Expect(ApprovalPrompts.Find("agy", screen("Allow calling this tool?", "> 1. Yes, allow tool call", "  3. No, deny tool call", Nav, Esc)) is null, "agy: numbers out of order");

    string[] claude(string pointed = "❯ 1. Yes", string question = "Do you want to proceed?", string command = "dotnet build Solforge.sln",
        string footer = "   Esc to cancel · Tab to amend") => screen(
        "╭──────────────────────────────────────────╮",
        "│ Bash command                              │",
        "│   " + command,
        "│ " + question,
        "│ " + pointed,
        "│   2. Yes, and don’t ask again for dotnet commands in C:/work",
        "│   3. No",
        "╰──────────────────────────────────────────╯",
        footer);
    var bash = ApprovalPrompts.Find("claude", claude());
    Expect(bash is { Choice: "Yes" }, "claude bash");
    Expect(ApprovalPrompts.Find("claude", claude())?.Signature == bash!.Signature, "claude: same prompt, same signature");
    Expect(ApprovalPrompts.Find("claude", claude(command: "git status"))?.Signature is { } status
        && ApprovalPrompts.Find("claude", claude(command: "git diff"))?.Signature is { } diff && status != diff, "claude: next command differs");
    Expect(ApprovalPrompts.Find("claude", claude("> 1. Yes")) is { Choice: "Yes" }, "claude ASCII pointer");
    Expect(ApprovalPrompts.Find("claude", claude(footer: "Esc to cancel · Tab to amend · ctrl+e to explain")) is { Choice: "Yes" }, "claude explain footer");
    foreach (string asked in new[] { "make this edit to", "create", "overwrite", "write to" })
        Expect(ApprovalPrompts.Find("claude", claude(question: $"Do you want to {asked} Foo.cs?")) is { Choice: "Yes" }, "claude " + asked);
    Expect(ApprovalPrompts.Find("claude", claude(question: "Do you want to allow Claude to fetch this content?")) is { Choice: "Yes" }, "claude fetch");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to make this edit to", "a/long/path/Foo.cs?", "❯ 1. Yes", "  2. No", "Esc to cancel"))
        is { Question: "Do you want to make this edit to a/long/path/Foo.cs?" }, "claude wrapped question");
    Expect(ApprovalPrompts.Find("claude", screen("Allow Claude in Chrome to click on example.com?", "❯ 1. Allow", "  2. Deny (esc)", "Esc to cancel")) is { Choice: "Allow" }, "claude chrome");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "  1. Yes", "  2. Yes, and don’t ask again for: git", "❯ 3. No", "Esc to cancel")) is null, "claude: No highlighted");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "❯ No", "  Yes", "Esc to cancel")) is null, "claude default-to-no");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "❯ 1. Yes", "  2. Maybe", "Esc to cancel")) is null, "claude: no decline sibling");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "❯ 1. Yes", "❯ 2. No", "Esc to cancel")) is null, "claude: two pointers");
    Expect(ApprovalPrompts.Find("claude", screen("Allow this read outside the working directories?", "❯ 1. Yes, and keep allowing any reads outside the working directories",
        "  2. No, and ask again next time", "Esc to cancel")) is null, "claude persistent first row");
    Expect(ApprovalPrompts.Find("claude", screen("Network request outside of sandbox", "Do you want to allow this connection?", "❯ 1. Yes", "  2. No", "Esc to cancel")) is null,
        "claude sandbox network stays manual");
    Expect(ApprovalPrompts.Find("claude", screen("Quick safety check: Is this a project you created or one you trust?", "❯ No, exit", "  Yes, I trust this folder",
        "Enter to confirm · Esc to cancel")) is null, "claude trust");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to create a new branch?", "❯ 1. Yes", "  2. No", "Enter to select · ↑/↓ to navigate · Esc to cancel")) is null,
        "claude model question, navigate footer");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to create a new branch?", "❯ 1. Yes", "  2. No", "Esc to cancel")) is null, "claude model question, not a file");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "❯ 1. Yes", "  2. No", "  3. Type something.", "  4. Chat about this", "Esc to cancel")) is null,
        "claude AskUserQuestion rows");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "Esc to cancel")) is null, "claude: question without options");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "Bash ran: dotnet test", "❯ 1. Yes", "  2. No", "Esc to cancel")) is null, "claude: text between question and options");
    Expect(ApprovalPrompts.Find("claude", screen("● The dialog asks: Do you want to proceed?", "  ❯ 1. Yes", "  2. No", "Esc to cancel then pick one")) is null, "claude: quoted question");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "❯ 1. Yes", "  2. No", "Esc to cancel", "",
        "● Done. The build passed.", "", "> Yes", "  ? for shortcuts")) is null, "claude: question in scrollback, output below");
    Expect(ApprovalPrompts.Find("claude", screen("● Should I go on?", "", "> Yes", "  ? for shortcuts")) is null, "claude: free-text input line");
    Expect(ApprovalPrompts.Find("claude", screen("● Should I go on?", "> Yes", "Esc to cancel")) is null, "claude: typed Yes above a footer");
    Expect(ApprovalPrompts.Find("claude", screen("Do you want to proceed?", "  1. Yes", "  2. No", "> Yes", "Esc to cancel")) is null, "claude: typed Yes under options");

    const uint Selected = 0x79b8ff;
    string[] cline(params string[] panel) => [.. screen([.. panel, "", " Auto-approve all disabled (Shift+Tab)"]).Select(line => line.PadRight(80))];
    uint[][] shades(string[] rows, uint fill, params string[] marked)
    {
        var cells = rows.Select(line => Enumerable.Repeat(fill, line.Length).ToArray()).ToArray();
        int row = Array.FindIndex(rows, line => line.Contains("[y] Approve"));
        foreach (string button in marked)
        {
            int start = rows[row].IndexOf(button, StringComparison.Ordinal) - 1;
            for (int i = start; i < start + (button == "[y]" ? 13 : 10); i++) cells[row][i] = Selected;
        }
        return cells;
    }
    const string Buttons = " [y] Approve   [n] Deny ";
    string[] asks = cline(" Cline needs permission", " Approve tool call?", " run_commands", "   $ dotnet test", Buttons);
    string[] after = cline(" Cline needs permission", " run_commands", "   $ dotnet test", " Approve tool call?", Buttons);
    string[] json = cline(" Cline needs permission", " Approve tool call?", " write_to_file", "   {", "     \"path\": \"scripts/a.lua\",",
        "     \"content\": \"-- new\"", "   }", Buttons);
    Expect(ApprovalPrompts.Find("cline", asks, shades(asks, 0, "[y]")) is { Choice: "Approve" }, "cline approve highlighted");
    Expect(ApprovalPrompts.Find("cline", after, shades(after, 0, "[y]")) is { Choice: "Approve" }, "cline question under the detail");
    Expect(ApprovalPrompts.Find("cline", json, shades(json, 0, "[y]")) is { Choice: "Approve" }, "cline multi-line detail");
    Expect(ApprovalPrompts.Find("cline", asks, shades(asks, 0x202020, "[y]")) is { Choice: "Approve" }, "cline filled panel");
    Expect(ApprovalPrompts.Find("cline", asks, shades(asks, 0, "[n]")) is null, "cline: Deny highlighted");
    Expect(ApprovalPrompts.Find("cline", asks, shades(asks, 0, "[y]", "[n]")) is null, "cline: both highlighted");
    Expect(ApprovalPrompts.Find("cline", asks) is null, "cline needs colours");
    string[] scrolled = [.. asks, .. Enumerable.Range(0, 6).Select(i => $" output line {i}".PadRight(80))];
    Expect(ApprovalPrompts.Find("cline", scrolled, shades(scrolled, 0, "[y]")) is null, "cline: panel in scrollback");
    string[] bare = cline(" Cline needs permission", " Approve tool call?", " run_commands");
    Expect(ApprovalPrompts.Find("cline", bare, bare.Select(line => new uint[line.Length]).ToArray()) is null, "cline: question without buttons");
    string[] followUp = cline(" Cline is asking a question", " Run the tests?", "> Yes", "  No", "  Type a response...");
    Expect(ApprovalPrompts.Find("cline", followUp, followUp.Select(line => Enumerable.Repeat(Selected, line.Length).ToArray()).ToArray()) is null, "cline follow-up question");
}

static class NativeArgs
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr memory);
}

/** A fixed scene: the find_object fixture, no viewport, and an approver that always declines. */
sealed class FixtureHost(string project) : ISolforgeHost
{
    public const string Context = """
        {"map":"Botza District","mission":"0x80001234","camera":{"position":[1,2,3],"zone":"crater"},
         "hovered":{"id":"w:1","label":"Botza District / Static mesh 7489 - Last city crater [182]"},
         "selected":[{"id":"w:1","label":"Botza District / Static mesh 7489 - Last city crater [182]","kind":"Mesh",
           "tag":"0x80A1B2C3","package":"w64_cosmodrome_0123","values":{"Name":"Last city crater","Scale":"1.5"}}],
         "open":{"asset":"crater rock 0x80FFEE01 (w64_shared_01)","script":"scripts/freeroam.lua","changes":3,"status":"Ready"},
         "pinned":[{"id":"w:4","label":"Tube 50"}],
         "recent":[{"kind":"select","id":"w:1","label":"Static mesh 7489","secondsAgo":2.5}]}
        """;
    public int Approvals;
    public bool Paused { get; set; }
    public string? ProjectRoot => project;
    public IReadOnlyList<string> SdkRoots => [];
    public string? CurrentMap => "Botza District";
    public AttentionLog Attention { get; } = new();
    public string ContextJson => Context;
    public IReadOnlyList<VisibleObject> Visible => [];
    public Task<IReadOnlyList<ObjectEntry>> ObjectsAsync(string query, CancellationToken cancel) =>
        Task.FromResult<IReadOnlyList<ObjectEntry>>(
        [
            new("w:1", "Botza District / Static mesh 7489 - Last city crater [182]", "Mesh", ObjectScope.Viewport, "Botza District"),
            new("w:3", "Botza District / Static mesh 74890 - Crater rim [183]", "Mesh", ObjectScope.Tree, "Botza District")
        ]);
    public Task<JsonObject?> GetObjectAsync(string id, CancellationToken cancel) => Task.FromResult<JsonObject?>(null);
    public Task<JsonObject> GetOpenDocumentsAsync(CancellationToken cancel) => Task.FromResult(new JsonObject
    {
        ["asset"] = new JsonObject { ["tag"] = "0x80A1B2C3", ["type"] = 8, ["package"] = "w64_cosmodrome_0123", ["name"] = "crater" },
        ["script"] = new JsonObject { ["path"] = "scripts/freeroam.lua", ["unsaved"] = false },
        ["changes"] = new JsonObject { ["count"] = 3, ["rows"] = new JsonArray() }
    });
    public Task<byte[]?> ScreenshotAsync(string? objectId, bool labels, CancellationToken cancel) => Task.FromResult<byte[]?>(null);
    public Task<JsonObject?> GetTagAsync(uint hash, CancellationToken cancel) => Task.FromResult<JsonObject?>(null);
    public Task<JsonObject?> GetPackageEntryAsync(string id, CancellationToken cancel) => Task.FromResult<JsonObject?>(null);
    public JsonObject LiveState() => new() { ["attached"] = false };
    public Task<bool> ApproveAsync(string tool, string summary, CancellationToken cancel) { Interlocked.Increment(ref Approvals); return Task.FromResult(false); }
    public Task<string> RunActionAsync(string tool, JsonObject args, CancellationToken cancel) => Task.FromResult("ran");
}
