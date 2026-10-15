using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * How Solforge starts Claude Code: its own MCP config (no project .mcp.json approval needed), a prompt hook
 * that injects the live context, a system prompt naming the Solforge tools, and every folder Solforge has open.
 */
public static class EngineLaunch
{
    // No double quotes or percent signs: the text passes through cmd.exe and npm's .cmd shim.
    public const string SystemPrompt =
        "You are running inside Solforge's AI workspace (the Project Sol game editor). When the user says 'this', 'that', " +
        "'here' or 'what I'm looking at', they mean Solforge's live context: call the solforge MCP tools get_context and " +
        "get_open_documents before searching files.";

    public sealed record ClaudeFiles(string McpConfig, string Settings);

    /** Writes the MCP config and the UserPromptSubmit hook settings for the project; @param folder defaults to the discovery folder. */
    public static ClaudeFiles WriteClaudeFiles(string projectRoot, string shimPath, string? folder = null)
    {
        folder ??= McpDiscovery.Folder;
        Directory.CreateDirectory(folder);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectRoot).ToUpperInvariant())))[..12];
        var files = new ClaudeFiles(Path.Combine(folder, $"claude-mcp-{key}.json"), Path.Combine(folder, $"claude-settings-{key}.json"));
        var server = new JsonObject
        {
            ["type"] = "stdio", ["command"] = shimPath, ["args"] = new JsonArray("--project", projectRoot), ["env"] = new JsonObject()
        };
        Write(files.McpConfig, new JsonObject { ["mcpServers"] = new JsonObject { [McpClientConfigs.ServerName] = server } });
        // Forward slashes and plain quotes read the same in cmd.exe and in bash, whichever runs the hook.
        string hook = $"\"{shimPath.Replace('\\', '/')}\" --context --project \"{projectRoot.Replace('\\', '/').TrimEnd('/')}\"";
        var command = new JsonObject { ["type"] = "command", ["command"] = hook, ["timeout"] = 5 };
        Write(files.Settings, new JsonObject
        {
            ["hooks"] = new JsonObject { ["UserPromptSubmit"] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(command) }) }
        });
        return files;
    }

    /** @param files Null when they could not be written: the engine still gets the prompt and the folders. */
    public static string ClaudeArguments(ClaudeFiles? files, IEnumerable<string> addDirs)
    {
        var args = new List<string>();
        if (files is not null) args.AddRange(["--mcp-config", files.McpConfig, "--settings", files.Settings]);
        args.AddRange(["--append-system-prompt", SystemPrompt]);
        foreach (string folder in addDirs) { args.Add("--add-dir"); args.Add(folder); }
        return string.Join(' ', args.Select(Quote));
    }

    /** The Antigravity CLI (agy) gets the same folders; its MCP entry comes from ~/.gemini/config/mcp_config.json. */
    public static string AgyArguments(IEnumerable<string> addDirs) =>
        string.Join(' ', addDirs.SelectMany(folder => new[] { "--add-dir", folder }).Select(Quote));

    /** cmd /s strips only the outer quotes, so the engine path and each argument keep theirs; /k keeps a prompt afterwards. */
    public static string CmdLine(string executable, string arguments, bool keep = true) =>
        $"cmd.exe /d /s /{(keep ? 'k' : 'c')} \"{Quote(executable)}{(arguments.Length > 0 ? " " + arguments : "")}\"";

    /** One argument as the Windows argv parser reads it back. */
    public static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '"']) < 0) return value;
        var text = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    /**
     * Folders the engine may read besides the project: the given ones that exist outside it, then each ancestor of the
     * project below the user profile holding a .claude folder or CLAUDE.md (~/.claude loads anyway). Without
     * ancestors only the given folders are kept, so a caller that already scanned them can leave some out.
     */
    public static IReadOnlyList<string> AddDirs(string projectRoot, IEnumerable<string?> folders, string? home = null,
        bool ancestors = true)
    {
        string root = Full(projectRoot), profile = Full(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var result = new List<string>();
        void Add(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            string full;
            try { full = Full(folder); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            if (!Directory.Exists(full) || Inside(full, root) || result.Any(other => Inside(full, other))) return;
            result.RemoveAll(other => Inside(other, full));
            result.Add(full);
        }
        foreach (string? folder in folders) Add(folder);
        if (!ancestors) return result;
        for (var parent = Directory.GetParent(root); parent is not null && !string.Equals(Full(parent.FullName), profile,
                 StringComparison.OrdinalIgnoreCase); parent = parent.Parent)
            if (Directory.Exists(Path.Combine(parent.FullName, ".claude")) || File.Exists(Path.Combine(parent.FullName, "CLAUDE.md")))
                Add(parent.FullName);
        return result;
    }

    /** The full path without a trailing separator, except on a drive root. */
    private static string Full(string path)
    {
        string full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
    }

    /** True when the path is the folder or below it. */
    private static bool Inside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static void Write(string path, JsonObject root)
    {
        string text = root.ToJsonString(McpJson.Indented);
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        File.WriteAllText(path, text);
    }
}
