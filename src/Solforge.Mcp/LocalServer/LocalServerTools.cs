/**
 * File: MCP tools for the local game server (local_server_status, log, subsystems, backups, config, deploy,
 * start/stop/restart, rollback, selftest, command, quick_action). Mutating tools ask for approval first.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp.LocalServer;

/**
 * Model-agnostic MCP tools for the local WSL sol-server: plain JSON-schema tools any MCP client can call. Reads run
 * at once; anything that changes the server asks for the user's Approve click first. Local only (never the VPS).
 */
public static class LocalServerTools
{
    public const string Instructions =
        " The local test server (WSL sol-server, never the VPS) has local_server_* tools: status (pid, ports, uptime, players, " +
        "ghost-member check), log tail with filter, subsystems, deploy the project build with backup and rollback, config, " +
        "selftest, and commands (give items, reset a character, set the next activity).";

    public static readonly string[] Names =
    [
        "local_server_status", "local_server_log", "local_server_subsystems", "local_server_backups", "local_server_config_read",
        "local_server_deploy_plan", "local_server_start", "local_server_stop", "local_server_restart", "local_server_deploy",
        "local_server_rollback", "local_server_config_write", "local_server_selftest", "local_server_command", "local_server_quick_action",
    ];

    /**
     * Builds the local server tool list; projectRoot supplies the open project and approve asks the user to confirm
     * risky actions.
     */
    public static IReadOnlyList<McpTool> Create(LocalServerManager server, Func<string?> projectRoot,
        Func<string, string, CancellationToken, Task<bool>> approve)
    {
        /** Shorthand for a JSON schema. */
        static JsonObject Schema(params (string, string, string, bool)[] properties) => McpJson.Schema(properties);
        /** Wraps a read-only tool so exceptions become tool errors. */
        static McpTool Read(string name, string description, JsonObject schema, Func<JsonObject, CancellationToken, Task<McpToolResult>> run) =>
            new(name, description, schema, false, async (args, cancel) =>
            {
                try { return await run(args, cancel).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or InvalidDataException or FormatException)
                { return McpToolResult.Error(error.Message); }
            });
        McpTool Act(string name, string description, JsonObject schema, Func<JsonObject, string?> validate, Func<JsonObject, string> summary,
            Func<JsonObject, CancellationToken, Task<ActionResult>> run) =>
            new(name, description, schema, true, async (args, cancel) =>
            {
                if (validate(args) is { } problem) return McpToolResult.Error(problem);
                if (!await approve(name, "Local server: " + summary(args), cancel).ConfigureAwait(false))
                    return McpToolResult.Error("The user declined this in Solforge.");
                try
                {
                    var result = await run(args, cancel).ConfigureAwait(false);
                    return result.Ok ? McpToolResult.Json(result.ToJson()) : new McpToolResult { IsError = true, Texts = { result.ToJson().ToJsonString(McpJson.Indented) } };
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or InvalidDataException or DirectoryNotFoundException)
                { return McpToolResult.Error(error.Message); }
            });
        /** Precondition that always passes. */
        static string? None(JsonObject _) => null;
        string? NeedProject(JsonObject _) => projectRoot() is null ? "Open a Solforge project first; deploy copies its scripts, sdk and build." : null;
        bool Restart(JsonObject args) => McpJson.Bool(args, "restart", false);

        return
        [
            Read("local_server_status", "Local WSL sol-server status: running, pid, uptime, which ports are listening (BAP UDP/TCP 30974, " +
                "web 8098, admin 8099), players, and the census ghost-member check (a member twice in one session breaks event spawns; " +
                "restart first). Also says if a sol-server from another folder is running.", Schema(),
                async (_, cancel) => McpToolResult.Json((await server.StatusAsync(cancel).ConfigureAwait(false)).ToJson())),
            Read("local_server_log", "The local server's stdout.log tail. filter: space-separated terms that must all match " +
                "(case-insensitive), '-term' excludes, 're:<regex>' for a regex. Example filters: 'error', 'stage=census', 'mission -debug'.",
                Schema(("filter", "string", "Terms or re:<regex>", false), ("lines", "integer", "Matching lines to return (default 100, max 2000)", false),
                    ("scan", "integer", "How many log lines to search back (default 4000, max 5000)", false)),
                async (args, cancel) =>
                {
                    var lines = await server.TailAsync(McpJson.String(args, "filter"), McpJson.Int(args, "lines", 100),
                        Math.Clamp(McpJson.Int(args, "scan", 4000), 1, 5000), cancel).ConfigureAwait(false);
                    return McpToolResult.Text(lines.Count == 0 ? "(no matching lines)" : string.Join("\n", lines));
                }),
            Read("local_server_subsystems", "What each server subsystem does (sign-in, lobbies, NAT relay and news replace Demonware; " +
                "the activity host, bubbles/physics and investment replace Bungie's hosts), its port, and whether it is up now.",
                Schema(("live", "boolean", "Also check the running server (default true)", false)),
                async (args, cancel) => McpToolResult.Json(new JsonObject
                {
                    ["subsystems"] = ServerSubsystems.ToJson(McpJson.Bool(args, "live", true) ? await server.StatusAsync(cancel).ConfigureAwait(false) : null),
                })),
            Read("local_server_backups", "Deploy backups on the local server, newest first, with file count and whether each was rolled back.", Schema(),
                async (_, cancel) =>
                {
                    var rows = new JsonArray();
                    foreach (var entry in await server.BackupsAsync(cancel).ConfigureAwait(false))
                        rows.Add(new JsonObject { ["id"] = entry.Id, ["files"] = entry.Files, ["rolled_back"] = entry.RolledBack, ["project"] = entry.Project });
                    return McpToolResult.Json(new JsonObject { ["backups"] = rows });
                }),
            Read("local_server_config_read", "List the local server's config files, or read one (config/settings.json, " +
                "config/dedicated_settings.json, config/sol_coop.cfg, config/sol_panel.cfg, Sunrise/crucible.cfg). Secret values are redacted.",
                Schema(("name", "string", "File to read; omit to list", false)),
                async (args, cancel) =>
                {
                    if (McpJson.String(args, "name") is not { Length: > 0 } name)
                    {
                        var files = new JsonArray();
                        foreach (string file in await server.ConfigFilesAsync(cancel).ConfigureAwait(false)) files.Add(file);
                        return McpToolResult.Json(new JsonObject { ["files"] = files });
                    }
                    return McpToolResult.Text(LocalServerManager.Redact(await server.ReadConfigAsync(name, cancel).ConfigureAwait(false)));
                }),
            Read("local_server_deploy_plan", "What local_server_deploy would copy from the open project: scripts/, sdk/, activity_sdk.pack " +
                "and build_data.bin (newest build folder first), with counts per folder. Changes nothing.", Schema(),
                (_, _) => Task.FromResult(projectRoot() is { } root ? McpToolResult.Json(DeployPlanner.Plan(root).ToJson())
                    : McpToolResult.Error("Open a Solforge project first."))),

            Act("local_server_start", "Start the local server (refuses if running; waits for the BAP port to be free first).", Schema(),
                None, _ => "start", (_, cancel) => server.StartAsync(cancel)),
            Act("local_server_stop", "Stop the local server (SIGTERM, then SIGKILL after 10 s; waits for the port).", Schema(),
                None, _ => "stop", (_, cancel) => server.StopAsync(cancel)),
            Act("local_server_restart", "Restart the local server race-free (stop, wait for the socket, start). Clears ghost members.", Schema(),
                None, _ => "restart", (_, cancel) => server.RestartAsync(cancel)),
            Act("local_server_deploy", "Deploy the open project's build to the local server (scripts, sdk, activity_sdk.pack, " +
                "build_data.bin). Files it overwrites are backed up first; local_server_rollback undoes it. Every deployed .lua is then loaded in " +
                "a Lua interpreter (errors reported now), and with restart the report says whether the server came back healthy (pid, ports).",
                Schema(("restart", "boolean", "Restart the server afterwards (default false)", false)),
                NeedProject, args => $"deploy {projectRoot()} to {server.Options.SunriseDirectory}" + (Restart(args) ? " and restart" : ""),
                (args, cancel) => server.DeployAsync(DeployPlanner.Plan(projectRoot()!), Restart(args), cancel)),
            Act("local_server_rollback", "Undo a deploy: restore the files it overwrote and delete the ones it added.",
                Schema(("backup", "string", "Backup id from local_server_backups (default: newest not rolled back)", false),
                    ("restart", "boolean", "Restart afterwards (default false)", false)),
                args => McpJson.String(args, "backup") is { Length: > 0 } id && id != "latest" && !DeployPlanner.IsBackupId(id) ? $"'{id}' is not a backup id." : null,
                args => $"roll back {McpJson.String(args, "backup") ?? "the newest deploy"}" + (Restart(args) ? " and restart" : ""),
                (args, cancel) => server.RollbackAsync(McpJson.String(args, "backup"), Restart(args), cancel)),
            Act("local_server_config_write", "Replace a local server config file (JSON is validated; the old file is backed up). Restart to apply.",
                Schema(("name", "string", "config/<file> or Sunrise/crucible.cfg", true), ("content", "string", "The whole new file", true)),
                args => LocalServerManager.ConfigPath(McpJson.String(args, "name")) is not { } path ? "Name a config file from local_server_config_read."
                    : McpJson.String(args, "content") is not { } content ? "Give the whole file as content." : LocalServerManager.ConfigProblem(path, content),
                args => $"write {McpJson.String(args, "name")} ({McpJson.String(args, "content")?.Length ?? 0} chars)",
                (args, cancel) => server.WriteConfigAsync(McpJson.String(args, "name")!, McpJson.String(args, "content")!, cancel)),
            Act("local_server_selftest", "Run sol-server --selftest (headless round-trips; exit 0 = pass). Takes a few minutes.", Schema(),
                None, _ => "run --selftest", (_, cancel) => server.SelftestAsync(cancel)),
            Act("local_server_command", "Run one server command as the operator console, e.g. 'who', 'give <player> glimmer 50000', " +
                "'clear inventory <player> confirm', 'activity find <text>', 'goto edz', 'subclasses <player>'. 'help' lists them.",
                Schema(("line", "string", "The command without the leading slash", true)),
                args => string.IsNullOrWhiteSpace(McpJson.String(args, "line")) ? "Give a command line." : null,
                args => "/" + McpJson.String(args, "line"),
                (args, cancel) => server.CommandAsync(McpJson.String(args, "line")!, cancel)),
            Act("local_server_quick_action", "A common test action: give (player, item, amount), reset_character (resets one of the " +
                "player's characters to new: all gear, level, subclass and progress; the server backs the record up first; " +
                "the player must be offline), set_activity (the next launch goes to an activity name or index), goto (a place).",
                Schema(("action", "string", "give | reset_character | set_activity | goto", true), ("player", "string", "Account name", false),
                    ("item", "string", "Item name or hash (give)", false), ("amount", "integer", "Amount (give, default 1)", false),
                    ("target", "string", "Activity or place (set_activity, goto)", false),
                    ("character", "string", "titan, hunter, warlock or slot 1-3 (reset_character; needed when the account has more than one)", false)),
                args => QuickLine(args).Problem, args => "/" + QuickLine(args).Line,
                (args, cancel) => server.CommandAsync(QuickLine(args).Line!, cancel)),
        ];
    }

    /** The server command a quick action runs, or why it cannot. */
    public static (string? Line, string? Problem) QuickLine(JsonObject args)
    {
        string action = McpJson.String(args, "action") ?? "";
        string? player = Word(McpJson.String(args, "player")), item = Word(McpJson.String(args, "item")), target = Word(McpJson.String(args, "target"));
        int amount = Math.Max(1, McpJson.Int(args, "amount", 1));
        string? character = Word(McpJson.String(args, "character"))?.ToLowerInvariant();
        return action switch
        {
            "give" when player is null || item is null => (null, "give needs player and item."),
            "give" => ($"give {player} {item} {amount}", null),
            "reset_character" when player is null => (null, "reset_character needs player."),
            "reset_character" when character is not null && character is not ("titan" or "hunter" or "warlock" or "1" or "2" or "3")
                => (null, "character must be titan, hunter, warlock or 1-3."),
            "reset_character" => ($"reset character {player}{(character is null ? "" : " " + character)} confirm", null),
            "set_activity" when target is null => (null, "set_activity needs target (name, index or 'find <text>')."),
            "set_activity" => ($"activity {target}", null),
            "goto" when target is null => (null, "goto needs target (tower, edz, moon...)."),
            "goto" => ($"goto {target}", null),
            _ => (null, "action must be give, reset_character, set_activity or goto."),
        };
    }

    /** Quotes names with spaces the way the server's command parser reads them. */
    private static string? Word(string? text)
    {
        text = text?.Trim().Replace("\"", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(text)) return null;
        return text.Contains(' ', StringComparison.Ordinal) && !text.StartsWith("find ", StringComparison.Ordinal) ? "\"" + text + "\"" : text;
    }
}
