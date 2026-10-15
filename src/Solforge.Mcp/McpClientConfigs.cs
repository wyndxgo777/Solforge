using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * Writes the "solforge" MCP server entry for Claude Code, Cline and Antigravity. Every entry runs the stdio
 * shim with --project, so it reaches the Solforge that has this project open; the session token never goes
 * into a config file. Other servers and settings in each file are kept.
 */
public static class McpClientConfigs
{
    public const string ServerName = "solforge";

    public sealed record Written(string Client, string Path);

    /** @param home The user profile folder; checks pass a scratch folder. */
    public static IReadOnlyList<Written> WriteAll(string projectRoot, string shimPath, string? home = null, string? appData = null)
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
        written.Add(new("Cline", clineCli));
        string clineCode = Path.Combine(appData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev");
        if (Directory.Exists(clineCode))
        {
            string path = Path.Combine(clineCode, "settings", "cline_mcp_settings.json");
            Merge(path, cline.DeepClone().AsObject());
            written.Add(new("Cline (VS Code)", path));
        }
        // Antigravity: ~/.gemini/antigravity/mcp_config.json.
        string antigravity = Path.Combine(home, ".gemini", "antigravity", "mcp_config.json");
        Merge(antigravity, new JsonObject { ["command"] = shimPath, ["args"] = Array(args) });
        written.Add(new("Antigravity", antigravity));
        return written;
    }

    /** Sets mcpServers.solforge and keeps the rest; a damaged file is kept beside as .bak and replaced. */
    public static void Merge(string path, JsonObject server)
    {
        var root = new JsonObject();
        if (File.Exists(path))
        {
            try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
            catch (JsonException) { File.Copy(path, path + ".bak", true); root = new JsonObject(); }
        }
        if (root["mcpServers"] is not JsonObject servers) root["mcpServers"] = servers = new JsonObject();
        servers[ServerName] = server;
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
