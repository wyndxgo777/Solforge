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
        "get_open_documents before searching files. .solforge/SESSION.md in the project holds the session context, and the " +
        "solforge-* skills are playbooks for common jobs (new weapon or armor, map bounds, patrol content, enemies, missions): " +
        "read the matching one first.";

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

    /**
     * Arguments that resume an engine's previous session, or "" when there is none (a first run starts fresh):
     * claude --continue (its latest session in the cwd), agy --conversation &lt;id&gt;, cline --id &lt;id&gt;.
     */
    public static string ResumeArguments(string engine, string? session) => session is null or "" ? "" : engine switch
    {
        "claude" => "--continue",
        "agy" => "--conversation " + Quote(session),
        "cline" => "--id " + Quote(session),
        _ => ""
    };

    /** Joins argument strings, skipping empty ones. */
    public static string JoinArguments(params string[] parts) => string.Join(' ', parts.Where(part => part.Length > 0));

    /** Claude Code keeps a folder's sessions in ~/.claude/projects/&lt;full path, non-alphanumerics as '-'&gt;/*.jsonl. */
    public static string ClaudeSessionsDir(string folder, string? home = null) => Path.Combine(
        home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects",
        new string(Full(folder).Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()));

    /** The newest Claude Code session id for the folder, or null. */
    public static string? ClaudeSession(string folder, string? home = null)
    {
        try
        {
            var dir = new DirectoryInfo(ClaudeSessionsDir(folder, home));
            return dir.Exists ? dir.EnumerateFiles("*.jsonl").MaxBy(file => file.LastWriteTimeUtc) is { } newest
                ? Path.GetFileNameWithoutExtension(newest.Name) : null : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /** The agy CLI logs each prompt to ~/.gemini/antigravity-cli/history.jsonl with its workspace and conversationId. */
    public static string? AgySession(string folder, string? home = null)
    {
        string root = Path.Combine(home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");
        try
        {
            string history = Path.Combine(root, "history.jsonl");
            if (!File.Exists(history)) return null;
            string? id = AgySession(folder, File.ReadLines(history));
            return id is not null && File.Exists(Path.Combine(root, "conversations", id + ".db")) ? id : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /** The last conversationId logged for this workspace in agy history lines (oldest first), or null. */
    public static string? AgySession(string folder, IEnumerable<string> historyLines)
    {
        string? found = null;
        foreach (string line in historyLines)
        {
            if (Parse(line) is not JsonObject entry) continue;
            if (Same(entry["workspace"], folder) && Text(entry["conversationId"]) is { Length: > 0 } id) found = id;
        }
        return found;
    }

    /** The newest top-level session for this folder in `cline history --json` output, or null. */
    public static string? ClineSession(string folder, string historyJson)
    {
        int start = historyJson.IndexOf('[');
        if (start < 0 || Parse(historyJson[start..]) is not JsonArray sessions) return null;
        return sessions.OfType<JsonObject>()
            .Where(session => (Same(session["cwd"], folder) || Same(session["workspaceRoot"], folder))
                && !(session["isSubagent"] is JsonValue sub && sub.TryGetValue(out bool isSub) && isSub))
            .Select(session => (Id: Text(session["sessionId"]), Started: Text(session["startedAt"]) ?? ""))
            .Where(session => session.Id is { Length: > 0 })
            .OrderByDescending(session => DateTimeOffset.TryParse(session.Started, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.MinValue)
            .Select(session => session.Id).FirstOrDefault();
    }

    private static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool Same(JsonNode? node, string folder)
    {
        if (Text(node) is not { Length: > 0 } path) return false;
        try { return string.Equals(Full(path), Full(folder), StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
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
