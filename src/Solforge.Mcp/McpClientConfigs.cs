using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * Writes the "solforge" MCP server entry for Claude Code, Cline, the Antigravity desktop app and the Antigravity
 * CLI (agy), which reads its own file under ~/.gemini/config. Every entry runs the stdio
 * shim with --project, so it reaches the Solforge that has this project open; the session token never goes
 * into a config file. Other servers and settings in each file are kept.
 */
public static class McpClientConfigs
{
    public const string ServerName = "solforge";

    /** One client config file that was written: client name and path. */
    public sealed record Written(string Client, string Path);

    /**
     * @param home The user profile folder; checks pass a scratch folder. @param servers When given (an engine start),
     * the Sol MCP set is also merged into the Cline and Antigravity CLI files (SolMcpServers.MergeInto).
     */
    public static IReadOnlyList<Written> WriteAll(string projectRoot, string shimPath, string? home = null, string? appData = null,
        IReadOnlyList<SolMcpServers.Server>? servers = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var written = new List<Written>();
        string[] args = ["--project", projectRoot];
        // Claude Code: project-scoped .mcp.json at the project root.
        string claude = Path.Combine(projectRoot, ".mcp.json");
        Merge(claude, new JsonObject { ["type"] = "stdio", ["command"] = shimPath, ["args"] = Array(args), ["env"] = new JsonObject() });
        written.Add(new("Claude Code", claude));
        // Cline CLI keeps its MCP settings under ~/.cline; the VS Code extension in its global storage (only if installed).
        var cline = new JsonObject { ["command"] = shimPath, ["args"] = Array(args), ["disabled"] = false, ["autoApprove"] = new JsonArray() };
        string clineCli = Path.Combine(home, ".cline", "data", "settings", "cline_mcp_settings.json");
        Merge(clineCli, cline);
        if (servers is not null) SolMcpServers.MergeInto(clineCli, SolMcpServers.Engine.Cline, servers);
        written.Add(new("Cline", clineCli));
        string clineCode = Path.Combine(appData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev");
        if (Directory.Exists(clineCode))
        {
            string path = Path.Combine(clineCode, "settings", "cline_mcp_settings.json");
            Merge(path, cline.DeepClone().AsObject());
            if (servers is not null) SolMcpServers.MergeInto(path, SolMcpServers.Engine.Cline, servers);
            written.Add(new("Cline (VS Code)", path));
        }
        // Antigravity desktop app: ~/.gemini/antigravity/mcp_config.json.
        string antigravity = Path.Combine(home, ".gemini", "antigravity", "mcp_config.json");
        Merge(antigravity, new JsonObject { ["command"] = shimPath, ["args"] = Array(args) });
        written.Add(new("Antigravity (desktop)", antigravity));
        // Antigravity CLI (agy): ~/.gemini/config/mcp_config.json, the shape "agy mcp add" writes.
        string agy = Path.Combine(home, ".gemini", "config", "mcp_config.json");
        Merge(agy, new JsonObject { ["command"] = shimPath, ["args"] = Array(args), ["disabled"] = false });
        if (servers is not null) SolMcpServers.MergeInto(agy, SolMcpServers.Engine.Agy, servers);
        written.Add(new("Antigravity CLI", agy));
        return written;
    }

    /**
     * Sets mcpServers.solforge and keeps the rest. An empty file (the agy installer leaves one) counts as {}; a
     * damaged one is kept beside as .bak and replaced.
     */
    public static void Merge(string path, JsonObject server) => Update(path, servers => servers[ServerName] = server);

    /**
     * Loads the file's mcpServers (created if missing), lets @param change edit it and writes the file only when the
     * text changed; every other key is kept.
     */
    public static void Update(string path, Action<JsonObject> change)
    {
        var root = new JsonObject();
        if (File.Exists(path) && File.ReadAllText(path) is { } existing && !string.IsNullOrWhiteSpace(existing))
        {
            try { root = JsonNode.Parse(existing) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { File.Copy(path, path + ".bak", true); root = new JsonObject(); }
        }
        if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = new JsonObject();
        change(servers);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string text = root.ToJsonString(McpJson.Indented);
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        File.WriteAllText(path, text);
    }

    private static JsonArray Array(string[] values)
    {
        var array = new JsonArray();
        foreach (string value in values) array.Add(value);
        return array;
    }
}
