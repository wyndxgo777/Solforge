// Multi-agent checks: several AI agents sharing one Solforge. The session registry (client name and shim pid, dead shims
// dropped), the open_map/close_map guard (another session's map, expiry, force, same session, the user opening another map),
// build/deploy activities with who started them, map-scoped reads through MultiAgentTools (map=<tag> answers from a background
// view and never reaches the UI's map), get_sessions, and McpHttpServer registering sessions from initialize + the pid header.
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Solforge.Mcp;

static class MultiAgentChecks
{
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("multi-agent: " + message);
    }

    private const uint OpenMap = 0x80000001, OtherMap = 0x80000002, SlowMap = 0x80000003;

    /** A presence with a settable open map and busy text; map 2 has a background view, map 3 is still loading. */
    private sealed class FakePresence : ISolforgePresence
    {
        public uint? OpenMapTag { get; set; } = OpenMap;
        public string? OpenMapName { get; set; } = "Open District";
        public string? BusyWork { get; set; }
        public List<(uint Map, uint? Mission)> Prepared { get; } = [];
        public Task<MapScopeResult> PrepareMapScopeAsync(uint map, uint? mission, CancellationToken cancel)
        {
            Prepared.Add((map, mission));
            return Task.FromResult(map == OpenMapTag ? MapScopeResult.Open()
                : map == OtherMap ? MapScopeResult.Ready("view:" + map.ToString("X8"), "Other Shore")
                : map == SlowMap ? MapScopeResult.Loading("Loading Slow Map in the background (12 entries read so far).")
                : MapScopeResult.Failed("No map; find it with list_maps."));
        }
    }

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static async Task Run()
    {
        Registry();
        await Tools();
        await Http();
    }

    private static void Registry()
    {
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var dead = new HashSet<int>();
        var registry = new McpSessionRegistry(() => now, pid => !dead.Contains(pid)) { MapGuard = TimeSpan.FromMinutes(10) };
        var presence = new FakePresence();
        var a = registry.Register("aaaaaaaa1111", "claude-code", "2.1", 111);
        var b = registry.Register("bbbbbbbb2222", "cline", null, 222);
        var c = registry.Register("cccccccc3333", " ", null, null);
        Expect(a.Label == "claude-code (pid 111)" && c.Label == "unknown client" && registry.Sessions().Count == 3, "labels and count");
        Expect(registry.Find("bbbbbbbb2222")?.Client == "cline" && registry.Find("nope") is null && registry.Find(null) is null, "find");
        Expect(registry.Find("cccccccc3333", 333)?.Pid == 333, "a later pid header fills a missing pid");

        // Nobody holds the map: anyone may open.
        Expect(registry.MapGuardProblem(b, presence, OtherMap, null, false, "open") is null, "no claim, no guard");
        registry.ClaimMap(a, OpenMap, null);
        string? problem = registry.MapGuardProblem(b, presence, OtherMap, null, false, "open");
        Expect(problem is not null && problem.Contains("claude-code (pid 111)") && problem.Contains("Open District") && problem.Contains("force=true")
            && problem.Contains("map=<tag>"), "another session is refused with who and how: " + problem);
        Expect(registry.MapGuardProblem(b, presence, null, null, false, "close") is { } closing && closing.Contains("close_map"), "close is guarded too");
        Expect(registry.MapGuardProblem(b, presence, OtherMap, null, true, "open") is null, "force passes");
        Expect(registry.MapGuardProblem(a, presence, OtherMap, null, false, "open") is null, "the same session passes");
        Expect(registry.MapGuardProblem(b, presence, OpenMap, null, false, "open") is null, "asking for the map already open passes");
        Expect(registry.MapGuardProblem(null, presence, OtherMap, null, false, "open") is not null, "an anonymous caller is refused");
        now = now.AddMinutes(11);
        Expect(registry.MapGuardProblem(b, presence, OtherMap, null, false, "open") is null, "the guard expires");
        now = now.AddMinutes(-11);

        // The user opens another map in the UI: the claim is gone once Solforge is idle, kept while it loads.
        presence.OpenMapTag = OtherMap;
        presence.BusyWork = "Loading Other Shore";
        Expect(registry.Claim(presence) is not null, "claim kept while loading");
        presence.BusyWork = null;
        Expect(registry.Claim(presence) is null && registry.MapGuardProblem(b, presence, OpenMap, null, false, "open") is null, "UI map change clears the claim");
        presence.OpenMapTag = OpenMap;

        // A mission-only open_map claims map 0 until the load says which map.
        registry.ClaimMap(b, 0, 0x80009999);
        presence.BusyWork = "Loading mission";
        Expect(registry.Claim(presence)?.MapTag == 0, "pending map while loading");
        presence.BusyWork = null;
        Expect(registry.Claim(presence) is { MapTag: OpenMap, SessionId: "bbbbbbbb2222" }, "pending map resolves to the loaded map");

        // Activities: background work runs until Solforge is idle; foreground ends with the reply; failures end at once.
        var build = registry.Begin(a, "build_project", "Build and install", true);
        registry.Replied(build, true, "Started; poll get_build_status.");
        presence.BusyWork = "Building";
        now = now.AddSeconds(5);
        Expect(registry.Running(presence).Contains(build), "build runs while busy");
        var lines = registry.PresenceLines(presence);
        Expect(lines.Any(line => line.Busy && line.Text.StartsWith("Build and install - started by claude-code (pid 111)", StringComparison.Ordinal)),
            "presence shows the build and who started it: " + string.Join(" | ", lines.Select(line => line.Text)));
        Expect(lines.Any(line => !line.Busy && line.Text.StartsWith("cline (pid 222) [bbbbbbbb] - has the map open", StringComparison.Ordinal)),
            "presence shows the map holder");
        var json = registry.ToJson(a, presence);
        Expect(json["running"]?.AsArray().Count == 1 && json["running"]![0]!["startedBy"]?.ToString() == "claude-code (pid 111)"
            && json["map"]?["openedBy"]?.ToString() == "cline (pid 222)" && json["you"]?["id"]?.ToString() == "aaaaaaaa1111", "get_sessions json: " + json.ToJsonString());
        presence.BusyWork = null;
        Expect(!registry.Running(presence).Contains(build) && build.Ended is not null, "build ends once idle");
        var deploy = registry.Begin(b, "local_server_deploy", "Local server deploy", false);
        Expect(registry.Running(presence).Contains(deploy), "deploy runs until it replies");
        registry.Replied(deploy, true, "Deployed");
        Expect(!deploy.Running, "deploy ends with its reply");
        var failed = registry.Begin(b, "build_project", "Build", true);
        registry.Replied(failed, false, "No project is open");
        Expect(!failed.Running && registry.Recent(5).Contains(failed), "a refused build ends at once");
        presence.BusyWork = "Reading maps and missions";
        Expect(registry.PresenceLines(presence).Any(line => line.Busy && line.Text.Contains("started in the Solforge UI")), "UI work is the user's");
        presence.BusyWork = null;

        // A dead shim and a long-idle session disappear; DELETE removes at once.
        dead.Add(222);
        Expect(registry.Sessions().All(s => s.Id != "bbbbbbbb2222"), "dead shim dropped");
        Expect(registry.PresenceLines(presence).Any(line => line.Text.Contains("(gone) opened the map")), "a gone holder is still named");
        registry.Remove("cccccccc3333");
        now = now.AddHours(3);
        Expect(registry.Sessions().Count == 0, "idle sessions forgotten and removed ones gone");
    }

    private static async Task Tools()
    {
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var registry = new McpSessionRegistry(() => now, _ => true);
        var presence = new FakePresence();
        var seen = new List<JsonObject>();
        McpTool Fake(string name, bool approval, Func<JsonObject, string> reply) =>
            new(name, name + " test tool.", McpJson.Schema(("id", "string", "id", false)), approval, (args, _) =>
            {
                seen.Add((JsonObject)args.DeepClone());
                return Task.FromResult(McpToolResult.Text(reply(args)));
            });
        var tools = MultiAgentTools.Wrap(
        [
            Fake("find_object", false, _ => "answered from " + (MapScope.Current?.ToString() ?? "the UI map")),
            Fake("get_placement", false, _ => "placement from " + (MapScope.Current?.ToString() ?? "the UI map")),
            Fake("open_map", true, args => "Opening map " + McpJson.String(args, "map") + "; poll get_editor_state until busy is false."),
            Fake("close_map", true, _ => "Closed the map; its object edits stay in the project."),
            Fake("build_project", true, _ => "Started; poll get_build_status."),
            Fake("read_project_file", false, _ => "file")
        ], presence, registry);
        var byName = tools.ToDictionary(tool => tool.Name);
        Expect(byName.ContainsKey("get_sessions") && !byName["get_sessions"].RequiresApproval, "get_sessions added, read-only");
        foreach (string name in new[] { "find_object", "get_placement" })
            Expect(byName[name].InputSchema["properties"]?["map"] is not null && byName[name].InputSchema["properties"]?["mission"] is not null
                && byName[name].Description.Contains("map=<tag>"), name + " takes map and mission");
        Expect(byName["open_map"].InputSchema["properties"]?["force"] is not null && byName["close_map"].InputSchema["properties"]?["force"] is not null
            && byName["read_project_file"].InputSchema["properties"]?["force"] is null, "open_map and close_map take force");
        Expect(byName["open_map"].RequiresApproval && byName["build_project"].RequiresApproval, "approval flags kept");

        var a = registry.Register("aaaaaaaa1111", "claude-code", "2", 111);
        var b = registry.Register("bbbbbbbb2222", "antigravity", "1", 222);
        async Task<McpToolResult> Call(McpSession? who, string tool, JsonObject args)
        {
            McpSessionRegistry.Current = who;
            try { return await byName[tool].Handler(args, CancellationToken.None); }
            finally { McpSessionRegistry.Current = null; }
        }

        // Map-scoped reads: another map answers from its view; the open map and no map use the UI path.
        var other = await Call(a, "find_object", new JsonObject { ["query"] = "generator", ["map"] = "0x80000002" });
        Expect(!other.IsError && other.Texts[0] == "answered from view:80000002" && other.Texts.Any(text => text.Contains("Other Shore")
            && text.Contains("did not change")), "map=other reads the background view: " + string.Join(" / ", other.Texts));
        Expect(seen[^1]["map"] is null && seen[^1]["query"]?.ToString() == "generator", "map and mission are stripped before the tool runs");
        Expect(MapScope.Current is null, "the scope does not leak");
        var same = await Call(a, "get_placement", new JsonObject { ["id"] = "w:1", ["map"] = "0x80000001" });
        Expect(same.Texts.Count == 1 && same.Texts[0] == "placement from the UI map", "map=open map uses the UI path");
        var plain = await Call(a, "find_object", new JsonObject { ["query"] = "x" });
        Expect(plain.Texts[0] == "answered from the UI map" && presence.Prepared.Count == 2, "no map argument never loads anything");
        var slow = await Call(a, "find_object", new JsonObject { ["query"] = "x", ["map"] = "0x80000003" });
        Expect(slow.IsError && slow.Texts[0].Contains("Loading Slow Map") && slow.Texts[0].Contains("unchanged"), "a loading map says call again");
        Expect((await Call(a, "find_object", new JsonObject { ["query"] = "x", ["map"] = "edz" })).IsError, "a map that is no tag is refused");
        Expect((await Call(a, "find_object", new JsonObject { ["query"] = "x", ["map"] = "0x80000002", ["mission"] = "nope" })).IsError, "a bad mission is refused");
        await Call(a, "find_object", new JsonObject { ["query"] = "x", ["map"] = "0x80000002", ["mission"] = "0x80001234" });
        Expect(presence.Prepared[^1] == (OtherMap, 0x80001234u), "mission reaches the view");
        Expect((await Call(a, "find_object", new JsonObject { ["query"] = "x", ["map"] = "0x80000009" })).Texts[0].Contains("list_maps"), "unknown map error");

        // open_map claims the map; another session is refused (nothing runs) until force or expiry.
        int before = seen.Count;
        var opened = await Call(a, "open_map", new JsonObject { ["map"] = "0x80000001" });
        Expect(!opened.IsError && registry.Claim(presence)?.SessionId == a.Id, "open_map claims for the caller");
        var refused = await Call(b, "open_map", new JsonObject { ["map"] = "0x80000002" });
        Expect(refused.IsError && refused.Texts[0].Contains("claude-code (pid 111)") && seen.Count == before + 1, "another session is refused before the tool runs");
        Expect((await Call(b, "close_map", new JsonObject())).IsError, "another session cannot close it either");
        Expect(!(await Call(a, "open_map", new JsonObject { ["map"] = "0x80000002" })).IsError, "the holder may open another map");
        presence.OpenMapTag = OtherMap;
        Expect((await Call(b, "open_map", new JsonObject { ["map"] = "0x80000001" })).IsError, "still refused after the holder moved on");
        var forced = await Call(b, "open_map", new JsonObject { ["map"] = "0x80000001", ["force"] = true });
        presence.OpenMapTag = OpenMap;
        Expect(!forced.IsError && registry.Claim(presence)?.SessionId == b.Id, "force replaces and claims for the new session");
        Expect(!(await Call(b, "close_map", new JsonObject())).IsError && registry.Claim(presence) is null, "close_map releases the claim");

        // Builds are recorded with who started them; get_sessions answers who you are.
        presence.BusyWork = "Building";
        await Call(b, "build_project", new JsonObject { ["install"] = true });
        var sessions = JsonNode.Parse((await Call(a, "get_sessions", new JsonObject())).Texts[0])!;
        Expect(sessions["you"]?["label"]?.ToString() == "claude-code (pid 111)" && sessions["sessions"]?.AsArray().Count == 2
            && sessions["running"]?.AsArray().Any(row => row?["summary"]?.ToString() == "Build and install"
                && row["startedBy"]?.ToString() == "antigravity (pid 222)") == true, "get_sessions: " + sessions.ToJsonString());
        Expect(registry.Find(b.Id)?.Calls >= 4 && registry.Find(b.Id)?.LastTool == "build_project", "calls counted per session");
        presence.BusyWork = null;
    }

    private static async Task Http()
    {
        var registry = new McpSessionRegistry();
        var dispatcher = new McpDispatcher(MultiAgentTools.Wrap([], null, registry), "0.1.0", "checks");
        await using var server = new McpHttpServer(dispatcher) { Sessions = registry };
        server.Start();
        using var http = new HttpClient();
        async Task<(string? Session, JsonNode? Reply)> Post(string body, string? session, int? pid)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
            if (session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
            if (pid is not null) request.Headers.TryAddWithoutValidation(McpSessionRegistry.PidHeader, pid.ToString());
            using var response = await http.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();
            return (response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.First() : null, text.Length > 0 ? JsonNode.Parse(text) : null);
        }
        static string Initialize(string client) =>
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{}," +
            $"\"clientInfo\":{{\"name\":\"{client}\",\"version\":\"9\"}}}}}}";
        const string Sessions = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"get_sessions\",\"arguments\":{}}}";
        int pid = Environment.ProcessId;
        var (first, _) = await Post(Initialize("claude-code"), null, pid);
        var (second, _) = await Post(Initialize("cline"), null, null);
        Expect(first is not null && second is not null && first != second, "initialize hands out session ids");
        var (_, reply) = await Post(Sessions, first, pid);
        var result = JsonNode.Parse(reply?["result"]?["content"]?[0]?["text"]?.ToString() ?? "null");
        Expect(result?["you"]?["label"]?.ToString() == $"claude-code (pid {pid})" && result?["sessions"]?.AsArray().Count == 2
            && result["sessions"]!.AsArray().Any(row => row?["label"]?.ToString() == "cline" && row["you"]?.GetValue<bool>() == false),
            "sessions from initialize and the pid header: " + reply?.ToJsonString());
        var (_, anonymous) = await Post(Sessions, null, null);
        Expect(JsonNode.Parse(anonymous?["result"]?["content"]?[0]?["text"]?.ToString() ?? "null")?["you"] is null, "no session header, no you");
        using (var delete = new HttpRequestMessage(HttpMethod.Delete, server.Url))
        {
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
            delete.Headers.TryAddWithoutValidation("Mcp-Session-Id", second);
            (await http.SendAsync(delete)).Dispose();
        }
        Expect(registry.Sessions().Count == 1 && registry.Sessions()[0].Id == first, "DELETE ends the session");
    }
}
