/**
 * File: The Sol MCP server set every engine started from the AI workspace gets (solforge, gemini, clinepass and the
 * workspace's other MCPs such as sol-testkit and tiger-mcp), a run copy of solforge-mcp.exe that a publish cannot
 * replace or kill, and the checks behind the AI panel's MCP warnings.
 */
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * Collects the MCP servers for an engine start. Solforge's own servers (solforge, gemini, clinepass) always run the
 * current shim; the others are carried as the user configured them: every stdio server in a .mcp.json at or above the
 * project, then sol-testkit and tiger-mcp from ~/.claude.json, agy's or Cline's MCP settings.
 */
public static class SolMcpServers
{
    public const string Testkit = "sol-testkit", Tiger = "tiger-mcp";

    /** Workspace MCPs looked up in the user's engine configs when no .mcp.json above the project has them. */
    public static readonly string[] Carried = [Testkit, Tiger];

    /** The engines Solforge starts with MCP servers. */
    public enum Engine { Claude, Agy, Cline }

    /**
     * One stdio server. @param Owned True for Solforge's own (shim) servers, which are rewritten on every start;
     * carried ones are only added where the engine's config does not name them yet.
     */
    public sealed record Server(string Name, string Command, IReadOnlyList<string> Args, JsonObject Env, bool Owned, string Source);

    /**
     * Every server for a start. @param gemini adds ask_gemini (agy installed); @param clinePass adds ask_clinepass
     * (setting on, cline installed); @param home the user profile (checks pass a scratch folder).
     */
    public static IReadOnlyList<Server> Collect(string projectRoot, string shimPath, bool gemini, bool clinePass, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var servers = new List<Server>
        {
            new(McpClientConfigs.ServerName, shimPath, ["--project", projectRoot], new JsonObject(), true, "Solforge")
        };
        if (gemini) servers.Add(new(AgyDelegate.ServerName, shimPath, ["--gemini"], new JsonObject(), true, "Solforge"));
        if (clinePass) servers.Add(new(ClineDelegate.ServerName, shimPath, ["--clinepass"], new JsonObject(), true, "Solforge"));
        var owned = new HashSet<string>([McpClientConfigs.ServerName, AgyDelegate.ServerName, ClineDelegate.ServerName], StringComparer.OrdinalIgnoreCase);
        void Add(string name, JsonNode? node, string source)
        {
            if (owned.Contains(name) || servers.Any(server => server.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
            if (Parse(name, node, source) is { } server) servers.Add(server);
        }
        // Every stdio server in a .mcp.json at or above the project (nearest wins).
        for (var folder = new DirectoryInfo(Path.GetFullPath(projectRoot)); folder is not null; folder = folder.Parent)
            if (Read(Path.Combine(folder.FullName, ".mcp.json"))?["mcpServers"] is JsonObject found)
                foreach (var (name, node) in found) Add(name, node, Path.Combine(folder.FullName, ".mcp.json"));
        // The named workspace MCPs from the user's own engine configs.
        string claudeJson = Path.Combine(home, ".claude.json");
        var sources = new List<(JsonObject? Servers, string Source)>();
        if (Read(claudeJson) is JsonObject claude)
        {
            if (claude["projects"] is JsonObject projects)
                foreach (var (key, value) in projects)
                    if (SamePath(key, projectRoot)) sources.Add((value?["mcpServers"] as JsonObject, claudeJson));
            sources.Add((claude["mcpServers"] as JsonObject, claudeJson));
        }
        foreach (string file in new[] { AgyConfig(home), ClineConfig(home) })
            sources.Add((Read(file)?["mcpServers"] as JsonObject, file));
        foreach (string name in Carried)
            foreach (var (found, source) in sources)
                if (found?[name] is { } node) { Add(name, node, source); break; }
        return servers;
    }

    /** The servers one engine gets: an engine never gets the server that delegates to itself. */
    public static IEnumerable<Server> For(Engine engine, IEnumerable<Server> servers) => servers.Where(server =>
        !(engine == Engine.Agy && server.Name == AgyDelegate.ServerName) && !(engine == Engine.Cline && server.Name == ClineDelegate.ServerName));

    /** The server as Claude Code, agy or Cline writes it. */
    public static JsonObject Entry(Engine engine, Server server)
    {
        var args = new JsonArray();
        foreach (string arg in server.Args) args.Add(arg);
        var entry = new JsonObject();
        if (engine == Engine.Claude) entry["type"] = "stdio";
        entry["command"] = server.Command;
        entry["args"] = args;
        if (engine == Engine.Claude || server.Env.Count > 0) entry["env"] = server.Env.DeepClone();
        if (engine != Engine.Claude) entry["disabled"] = false;
        if (engine == Engine.Cline) entry["autoApprove"] = new JsonArray();
        return entry;
    }

    /** Claude Code's --mcp-config body: Solforge writes this file itself, so it holds exactly the given servers. */
    public static JsonObject ClaudeConfig(IEnumerable<Server> servers)
    {
        var all = new JsonObject();
        foreach (var server in For(Engine.Claude, servers)) all[server.Name] = Entry(Engine.Claude, server);
        return new JsonObject { ["mcpServers"] = all };
    }

    /**
     * Merges the servers into an engine's own MCP settings file and returns the names it kept as the user set them.
     * Solforge's servers replace a missing entry or an older solforge-mcp one (a moved or republished shim); a user's
     * own server under the same name, a carried name already present and every other server stay as they are.
     */
    public static IReadOnlyList<string> MergeInto(string path, Engine engine, IEnumerable<Server> servers)
    {
        var kept = new List<string>();
        McpClientConfigs.Update(path, all =>
        {
            foreach (var server in For(engine, servers))
            {
                if (all[server.Name] is JsonObject existing
                    && (!server.Owned || server.Name != McpClientConfigs.ServerName && !IsShimEntry(existing)))
                {
                    kept.Add(server.Name);
                    continue;
                }
                all[server.Name] = Entry(engine, server);
            }
        });
        return kept;
    }

    /** True when the entry runs solforge-mcp (any copy or path), so Solforge may refresh it. */
    public static bool IsShimEntry(JsonObject entry) =>
        entry["command"] is JsonValue value && value.TryGetValue(out string? command)
        && Path.GetFileName(command.Trim('"')).StartsWith("solforge-mcp", StringComparison.OrdinalIgnoreCase);

    /** ~/.gemini/config/mcp_config.json, the Antigravity CLI's MCP file. */
    public static string AgyConfig(string home) => Path.Combine(home, ".gemini", "config", "mcp_config.json");

    /** ~/.cline/data/settings/cline_mcp_settings.json, the Cline CLI's MCP file. */
    public static string ClineConfig(string home) => Path.Combine(home, ".cline", "data", "settings", "cline_mcp_settings.json");

    /**
     * The shim the engines run: a copy of the published solforge-mcp.exe under @param folder named after its size and
     * time (solforge-mcp-&lt;stamp&gt;.exe). publish.ps1 overwrites, and killing solforge-mcp before a publish ends,
     * only the published file, so running sessions keep their servers and a new start never reads a half-written exe.
     * Falls back to the newest earlier copy while the published one is being written, then to the published one.
     */
    public static string RunCopy(string published, string folder)
    {
        try
        {
            var source = new FileInfo(published);
            if (!source.Exists || !published.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return published;
            Directory.CreateDirectory(folder);
            string stamp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes($"{source.Length}:{source.LastWriteTimeUtc.Ticks}")))[..10].ToLowerInvariant();
            string target = Path.Combine(folder, $"solforge-mcp-{stamp}.exe");
            if (!(File.Exists(target) && new FileInfo(target).Length == source.Length))
            {
                // A publish holds the file open for writing: the copy fails and the last good copy runs instead.
                string temporary = target + "." + Environment.ProcessId + ".tmp";
                using (var input = new FileStream(published, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = File.Create(temporary))
                    input.CopyTo(output);
                File.Move(temporary, target, true);
            }
            foreach (var old in new DirectoryInfo(folder).EnumerateFiles("solforge-mcp-*.exe")
                         .Where(file => !file.FullName.Equals(target, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(file => file.LastWriteTimeUtc).Skip(2))
                try { old.Delete(); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            return target;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                return new DirectoryInfo(folder).Exists
                    ? new DirectoryInfo(folder).EnumerateFiles("solforge-mcp-*.exe").MaxBy(file => file.LastWriteTimeUtc)?.FullName ?? published
                    : published;
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { return published; }
        }
    }

    /**
     * File checks for each server: the command (a full path, or found on @param path with PATHEXT) and every
     * fully qualified argument (a script, package folder or project) must exist. One line per problem.
     */
    public static IReadOnlyList<string> CheckFiles(IEnumerable<Server> servers, string path, string? pathExt = null)
    {
        var problems = new List<string>();
        foreach (var server in servers)
        {
            if (Path.IsPathFullyQualified(server.Command) ? !File.Exists(server.Command) : EngineCommands.Resolve(server.Command, path, pathExt) is null)
                problems.Add($"{server.Name}: {server.Command} not found ({server.Source})");
            foreach (string arg in server.Args)
                if (Path.IsPathFullyQualified(arg) && !File.Exists(arg) && !Directory.Exists(arg))
                    problems.Add($"{server.Name}: {arg} not found ({server.Source})");
        }
        return problems;
    }

    /**
     * Starts the server, sends initialize and waits for its reply: null when it answers with serverInfo, else what
     * went wrong. The process is killed afterwards.
     */
    public static async Task<string?> Probe(string command, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(command)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false)
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        Process process;
        try { process = Process.Start(info) ?? throw new InvalidOperationException("no process"); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { return "could not start: " + exception.Message; }
        using (process)
        {
            using var cancel = new CancellationTokenSource(timeout);
            try
            {
                _ = process.StandardError.ReadToEndAsync(cancel.Token);
                await process.StandardInput.WriteLineAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\","
                    + "\"capabilities\":{},\"clientInfo\":{\"name\":\"solforge-check\",\"version\":\"1\"}}}");
                await process.StandardInput.FlushAsync();
                while (await process.StandardOutput.ReadLineAsync(cancel.Token) is { } line)
                {
                    JsonNode? reply;
                    try { reply = JsonNode.Parse(line); }
                    catch (JsonException) { continue; }
                    if (reply?["id"]?.ToJsonString() != "1") continue;
                    if (reply["result"]?["serverInfo"] is not null) return null;
                    return reply["error"]?["message"]?.ToString() ?? "no serverInfo in its reply";
                }
                return "exited without answering initialize";
            }
            catch (OperationCanceledException) { return $"no answer to initialize within {timeout.TotalSeconds:0} s"; }
            catch (IOException exception) { return process.HasExited ? "exited without answering initialize" : exception.Message; }
            finally
            {
                try { if (!process.HasExited) process.Kill(true); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    /** A stdio entry in a config file as a server, or null (disabled, remote or no command). */
    private static Server? Parse(string name, JsonNode? node, string source)
    {
        if (node is not JsonObject entry || entry["command"] is not JsonValue command || !command.TryGetValue(out string? text)
            || text.Length == 0 || entry["disabled"]?.GetValueKind() == JsonValueKind.True) return null;
        if (entry["type"] is JsonValue type && type.TryGetValue(out string? kind) && kind != "stdio") return null;
        var args = entry["args"] is JsonArray array
            ? array.Select(arg => arg is JsonValue value && value.TryGetValue(out string? s) ? s : arg?.ToJsonString() ?? "").ToList()
            : new List<string>();
        return new(name, text, args, entry["env"] is JsonObject env ? env.DeepClone().AsObject() : new JsonObject(), false, source);
    }

    /** A JSON file read while its owner may be writing it, or null (missing, empty or damaged). */
    private static JsonNode? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /** Whether two paths name the same folder (~/.claude.json keys use forward slashes). */
    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
