// Sol MCP set checks: which servers an engine start collects (Solforge's own, .mcp.json above the project, the named
// workspace MCPs from the user's engine configs), the per-engine entries and Claude's --mcp-config, merging into agy and
// Cline files without touching the user's own servers, the solforge-mcp run copy, and the file and initialize checks.
using System.Text.Json.Nodes;
using Solforge.Mcp;

static class McpSetChecks
{
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("mcp set: " + message);
    }

    /** Entry point: runs every check in this class and throws on the first failure. @param shimDll the built shim. */
    public static async Task Run(string scratch, string shimDll)
    {
        string home = Path.Combine(scratch, "home"), top = Path.Combine(scratch, "sol top");
        string project = Path.Combine(top, "10-Project Sol", "Project Sol");
        string tiger = Path.Combine(top, "tiger", "tiger-mcp.exe"), script = Path.Combine(top, "testkit", "sol_testkit_mcp.py");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(Path.GetDirectoryName(tiger)!);
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(tiger, "");
        File.WriteAllText(script, "");
        string shim = Path.Combine(scratch, "run", "solforge-mcp-abc.exe");

        // The workspace .mcp.json: tiger-mcp is carried; its stale solforge, a disabled and a remote server are not.
        File.WriteAllText(Path.Combine(top, ".mcp.json"), new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["solforge"] = new JsonObject { ["command"] = @"C:\old\publish\solforge-mcp.exe" },
                ["tiger-mcp"] = new JsonObject { ["type"] = "stdio", ["command"] = tiger, ["args"] = new JsonArray("-v", "d1_roi"), ["env"] = new JsonObject() },
                ["off"] = new JsonObject { ["command"] = "x.exe", ["disabled"] = true },
                ["remote"] = new JsonObject { ["type"] = "http", ["url"] = "https://example.invalid/mcp" }
            }
        }.ToJsonString());
        // sol-testkit only in ~/.claude.json (project entry, forward slashes): found there with its env.
        File.WriteAllText(Path.Combine(home, ".claude.json"), new JsonObject
        {
            ["projects"] = new JsonObject
            {
                [project.Replace('\\', '/')] = new JsonObject
                {
                    ["mcpServers"] = new JsonObject
                    {
                        ["sol-testkit"] = new JsonObject
                        {
                            ["type"] = "stdio", ["command"] = "py", ["args"] = new JsonArray("-3", script),
                            ["env"] = new JsonObject { ["SOL_PAD_PORT"] = "48811" }
                        }
                    }
                }
            },
            ["mcpServers"] = new JsonObject { ["sol-testkit"] = new JsonObject { ["command"] = "loses.exe" }, ["mine"] = new JsonObject { ["command"] = "m.exe" } }
        }.ToJsonString());

        var servers = SolMcpServers.Collect(project, shim, gemini: true, clinePass: true, home);
        Expect(servers.Select(server => server.Name).SequenceEqual(["solforge", "gemini", "clinepass", "tiger-mcp", "sol-testkit"]),
            "collected: " + string.Join(", ", servers.Select(server => server.Name)));
        Expect(servers.Take(3).All(server => server.Owned && server.Command == shim) && servers.Skip(3).All(server => !server.Owned), "owned");
        Expect(servers[0].Args.SequenceEqual(["--project", project]) && servers[1].Args.SequenceEqual(["--gemini"]), "shim args");
        Expect(servers[4].Command == "py" && servers[4].Env["SOL_PAD_PORT"]?.ToString() == "48811", "testkit from .claude.json project entry");
        var bare = SolMcpServers.Collect(project, shim, gemini: false, clinePass: false, home);
        Expect(bare.Select(server => server.Name).SequenceEqual(["solforge", "tiger-mcp", "sol-testkit"]), "no gemini or clinepass unless asked");

        // An engine never gets its own delegate.
        Expect(!SolMcpServers.For(SolMcpServers.Engine.Agy, servers).Any(server => server.Name == "gemini"), "agy without gemini");
        Expect(!SolMcpServers.For(SolMcpServers.Engine.Cline, servers).Any(server => server.Name == "clinepass"), "cline without clinepass");
        Expect(SolMcpServers.For(SolMcpServers.Engine.Claude, servers).Count() == 5, "claude gets all");

        // Claude's --mcp-config holds the whole set in Claude's shape.
        var claude = SolMcpServers.ClaudeConfig(servers)["mcpServers"]!.AsObject();
        Expect(claude.Count == 5 && claude.All(pair => pair.Value?["type"]?.ToString() == "stdio" && pair.Value?["env"] is JsonObject), "claude shape");
        Expect(claude["tiger-mcp"]?["command"]?.ToString() == tiger && claude["sol-testkit"]?["env"]?["SOL_PAD_PORT"]?.ToString() == "48811", "claude carried");
        string folder = Path.Combine(scratch, "claude-files");
        var files = EngineLaunch.WriteClaudeFiles(project, shim, folder, servers: servers);
        Expect(files.Gemini && files.ClinePass && JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(files.McpConfig)), SolMcpServers.ClaudeConfig(servers)),
            "claude files from the set");
        var plain = EngineLaunch.WriteClaudeFiles(project, shim, folder, servers: bare);
        Expect(!plain.Gemini && !plain.ClinePass && !EngineLaunch.ClaudeArguments(plain, []).Contains("ask_gemini"), "flags follow the set");

        // Cline: the user's rea, their own gemini and their tiger-mcp stay; the old solforge is refreshed; testkit joins.
        string cline = SolMcpServers.ClineConfig(home);
        Directory.CreateDirectory(Path.GetDirectoryName(cline)!);
        var rea = new JsonObject { ["command"] = "cmd.exe", ["args"] = new JsonArray("/c", "npx", "rea"), ["disabled"] = false, ["autoApprove"] = new JsonArray() };
        var userGemini = new JsonObject { ["command"] = "npx", ["args"] = new JsonArray("gemini-mcp") };
        var userTiger = new JsonObject { ["command"] = tiger, ["args"] = new JsonArray("-v", "d2") };
        File.WriteAllText(cline, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["rea"] = rea.DeepClone(), ["gemini"] = userGemini.DeepClone(), ["tiger-mcp"] = userTiger.DeepClone(),
                ["solforge"] = new JsonObject { ["command"] = @"C:\old\publish\solforge-mcp.exe", ["args"] = new JsonArray("--project", "old") }
            },
            ["other"] = 1
        }.ToJsonString());
        var kept = SolMcpServers.MergeInto(cline, SolMcpServers.Engine.Cline, servers);
        var clineRoot = JsonNode.Parse(File.ReadAllText(cline))!;
        var clineServers = clineRoot["mcpServers"]!.AsObject();
        Expect(kept.SequenceEqual(["gemini", "tiger-mcp"]), "kept: " + string.Join(", ", kept));
        Expect(JsonNode.DeepEquals(clineServers["rea"], rea) && JsonNode.DeepEquals(clineServers["gemini"], userGemini)
            && JsonNode.DeepEquals(clineServers["tiger-mcp"], userTiger) && clineRoot["other"]?.GetValue<int>() == 1, "user servers untouched");
        Expect(clineServers["solforge"]?["command"]?.ToString() == shim && clineServers["solforge"]?["autoApprove"] is JsonArray, "solforge refreshed");
        Expect(clineServers["sol-testkit"]?["command"]?.ToString() == "py" && clineServers["sol-testkit"]?["env"]?["SOL_PAD_PORT"] is not null
            && clineServers["clinepass"] is null, "testkit added, no clinepass in cline");
        string before = File.ReadAllText(cline);
        SolMcpServers.MergeInto(cline, SolMcpServers.Engine.Cline, servers);
        Expect(File.ReadAllText(cline) == before, "cline merge is idempotent");

        // agy: an older solforge-mcp clinepass entry is refreshed to the current shim.
        string agy = SolMcpServers.AgyConfig(home);
        Directory.CreateDirectory(Path.GetDirectoryName(agy)!);
        File.WriteAllText(agy, "{\"mcpServers\":{\"clinepass\":{\"command\":\"C:\\\\old\\\\solforge-mcp.exe\",\"args\":[\"--clinepass\"]}}}");
        Expect(SolMcpServers.MergeInto(agy, SolMcpServers.Engine.Agy, servers).Count == 0, "agy keeps nothing");
        var agyServers = JsonNode.Parse(File.ReadAllText(agy))!["mcpServers"]!.AsObject();
        Expect(agyServers["clinepass"]?["command"]?.ToString() == shim && agyServers["gemini"] is null
            && agyServers["tiger-mcp"]?["disabled"]?.GetValue<bool>() == false && agyServers["tiger-mcp"]?["autoApprove"] is null, "agy entries");

        // WriteAll with the set: the agy and Cline CLI files get it; the desktop app keeps only solforge.
        string home2 = Path.Combine(scratch, "home2"), appData = Path.Combine(scratch, "appdata2");
        McpClientConfigs.WriteAll(project, shim, home2, appData, servers);
        Expect(JsonNode.Parse(File.ReadAllText(SolMcpServers.AgyConfig(home2)))?["mcpServers"]?["sol-testkit"] is not null
            && JsonNode.Parse(File.ReadAllText(SolMcpServers.ClineConfig(home2)))?["mcpServers"]?["gemini"] is not null, "WriteAll merges the set");
        var desktop = JsonNode.Parse(File.ReadAllText(Path.Combine(home2, ".gemini", "antigravity", "mcp_config.json")))!["mcpServers"]!.AsObject();
        Expect(desktop.Count == 1 && desktop["solforge"] is not null, "desktop app unchanged");

        // File checks: py on PATH and existing paths pass; a missing exe, script or project is reported.
        string bin = Path.Combine(scratch, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "py.exe"), "");
        Directory.CreateDirectory(Path.GetDirectoryName(shim)!);
        File.WriteAllText(shim, "");
        Expect(SolMcpServers.CheckFiles(servers, bin, ".EXE").Count == 0, "all present: " + string.Join(" | ", SolMcpServers.CheckFiles(servers, bin, ".EXE")));
        File.Delete(script);
        File.Delete(shim);
        var problems = SolMcpServers.CheckFiles(servers, Path.Combine(scratch, "nothing"), ".EXE");
        Expect(problems.Count(line => line.StartsWith("solforge: ") || line.StartsWith("gemini: ") || line.StartsWith("clinepass: ")) == 3
            && problems.Any(line => line.StartsWith("sol-testkit: py not found")) && problems.Any(line => line.StartsWith("sol-testkit: " + script)),
            "problems: " + string.Join(" | ", problems));

        // Run copy: named after the published exe, reused, replaced when it changes, the last copy while it is locked.
        string publish = Path.Combine(scratch, "publish", "solforge-mcp.exe"), runs = Path.Combine(scratch, "runs");
        Directory.CreateDirectory(Path.GetDirectoryName(publish)!);
        File.WriteAllText(publish, "v1");
        string first = SolMcpServers.RunCopy(publish, runs);
        Expect(Path.GetDirectoryName(first) == runs && Path.GetFileName(first).StartsWith("solforge-mcp-") && File.ReadAllText(first) == "v1", "copy: " + first);
        Expect(SolMcpServers.RunCopy(publish, runs) == first, "reused");
        File.WriteAllText(publish, "v2 longer");
        File.SetLastWriteTimeUtc(publish, DateTime.UtcNow.AddMinutes(1));
        string second = SolMcpServers.RunCopy(publish, runs);
        Expect(second != first && File.ReadAllText(second) == "v2 longer", "new copy after a publish");
        File.WriteAllText(publish, "v3 is being written");
        using (new FileStream(publish, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            File.SetLastWriteTimeUtc(second, DateTime.UtcNow.AddMinutes(2));
            Expect(SolMcpServers.RunCopy(publish, runs) == second, "locked publish uses the last copy");
        }
        Expect(SolMcpServers.RunCopy(Path.Combine(scratch, "gone", "solforge-mcp.exe"), runs).EndsWith(@"gone\solforge-mcp.exe"), "missing stays missing");
        for (int i = 3; i < 7; i++)
        {
            File.WriteAllText(publish, new string('v', i * 3));
            SolMcpServers.RunCopy(publish, runs);
        }
        Expect(Directory.GetFiles(runs, "solforge-mcp-*.exe").Length <= 3 && Directory.GetFiles(runs, "*.tmp").Length == 0, "old copies pruned");

        // Initialize probe: the real shim's gemini server answers; a missing command and an early exit are reported.
        Expect(await SolMcpServers.Probe("dotnet", [shimDll, "--gemini"], TimeSpan.FromSeconds(60)) is null, "shim answers initialize");
        Expect((await SolMcpServers.Probe(Path.Combine(scratch, "nothing.exe"), [], TimeSpan.FromSeconds(5)))?.StartsWith("could not start") == true, "missing command");
        Expect(await SolMcpServers.Probe("cmd.exe", ["/d", "/c", "exit"], TimeSpan.FromSeconds(10)) == "exited without answering initialize", "early exit");
    }
}
