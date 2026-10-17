/**
 * File: several AI agents sharing one Solforge. MultiAgentTools.Wrap wraps the finished tool list (it does not change any
 * tool's own code):
 *  - the map and object reads (find_object, get_object, list_mission_objects, get_mission_object, find_world_objects,
 *    get_placement) take map=<tag> (and mission=<tag>) and answer from a background copy of that map (MapScope), so one
 *    agent's reads never replace the map another agent has open in the UI;
 *  - open_map and close_map refuse, unless force=true, when another MCP session opened the UI's map less than
 *    McpSessionRegistry.MapGuard ago;
 *  - builds, deploys, map loads and server actions are recorded with who started them;
 *  - get_sessions lists the connected sessions (client name and shim pid), the map holder and the running work.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** The multi-agent layer over the tool list (see the file header). */
public static class MultiAgentTools
{
    /** Reads that accept map=<tag> and then answer from a background-loaded copy of that map. */
    public static readonly string[] MapScopedTools =
        ["find_object", "get_object", "list_mission_objects", "get_mission_object", "find_world_objects", "get_placement"];

    /** Tools that replace or close the UI's map; guarded against taking another session's map. */
    public static readonly string[] MapGuardedTools = ["open_map", "close_map"];

    /** Tools recorded as activities with who started them; true means the work goes on after the reply (poll-style). */
    public static readonly IReadOnlyDictionary<string, bool> ActivityTools = new Dictionary<string, bool>
    {
        ["build_project"] = true, ["build_to_folder"] = true, ["update_running_game"] = true, ["open_map"] = true,
        ["local_server_deploy"] = false, ["local_server_rollback"] = false, ["local_server_restart"] = false,
        ["local_server_start"] = false, ["local_server_stop"] = false, ["local_server_selftest"] = false
    };

    /** Appended to the server instructions. */
    public const string Instructions =
        " Several AI agents may share this Solforge: get_sessions shows who is connected (client and pid), who opened the map " +
        "the UI shows and which build or deploy is running and who started it. To read a map other than the open one, pass " +
        "map=<tag from list_maps> (and mission=<tag>) to find_object, get_object, list_mission_objects, get_mission_object, " +
        "find_world_objects or get_placement: they answer from a background copy and never change the UI. open_map and close_map " +
        "refuse to replace a map another session opened in the last few minutes unless you pass force=true.";

    /** Wraps every tool (call counting, guards, activities, map scope) and adds get_sessions. */
    public static IReadOnlyList<McpTool> Wrap(IReadOnlyList<McpTool> tools, ISolforgePresence? presence, McpSessionRegistry registry)
    {
        var list = new List<McpTool>(tools.Count + 1);
        foreach (var tool in tools) list.Add(WrapOne(tool, presence, registry));
        if (!tools.Any(tool => tool.Name == "get_sessions")) list.Add(GetSessions(presence, registry));
        return list;
    }

    /** get_sessions: read-only, works while context is paused (it says who is here, not what the user looks at). */
    public static McpTool GetSessions(ISolforgePresence? presence, McpSessionRegistry registry) =>
        new("get_sessions", "Who is using this Solforge right now: every connected MCP session (client name, shim pid, idle time, " +
            "last tool; you=true is you), which session opened the map the UI shows and how long the open_map guard still holds, " +
            "the build, deploy or map load running now and who started it, and recent finished work.",
            McpJson.Schema(), false,
            (_, _) => Task.FromResult(McpToolResult.Json(registry.ToJson(McpSessionRegistry.Current, presence))));

    private static McpTool WrapOne(McpTool tool, ISolforgePresence? presence, McpSessionRegistry registry)
    {
        bool scoped = MapScopedTools.Contains(tool.Name), guarded = MapGuardedTools.Contains(tool.Name);
        bool tracked = ActivityTools.TryGetValue(tool.Name, out bool background);
        var schema = tool.InputSchema;
        string description = tool.Description;
        if (scoped || guarded)
        {
            schema = (JsonObject)schema.DeepClone();
            if (schema["properties"] is not JsonObject properties) schema["properties"] = properties = new JsonObject();
            if (scoped)
            {
                properties["map"] = new JsonObject { ["type"] = "string", ["description"] =
                    "Optional map tag (list_maps): answer from a background copy of that map without changing the map the UI shows" };
                properties["mission"] = new JsonObject { ["type"] = "string", ["description"] =
                    "Optional mission tag (list_missions) to load on that map, for mission objects" };
                description += " map=<tag> reads another map from a background copy; the UI's map does not change.";
            }
            else
            {
                properties["force"] = new JsonObject { ["type"] = "boolean", ["description"] =
                    "Replace the map even though another agent's session opened it a few minutes ago (default false)" };
                description += " Refused while another agent's session opened the current map recently, unless force=true (get_sessions).";
            }
        }
        return tool with
        {
            InputSchema = schema,
            Description = description,
            Handler = async (args, cancel) =>
            {
                var caller = McpSessionRegistry.Current;
                registry.NoteCall(caller, tool.Name);
                if (scoped && !string.IsNullOrWhiteSpace(McpJson.String(args, "map")))
                    return await ScopedAsync(tool, args, presence, cancel).ConfigureAwait(false);
                uint? map = SolforgeTools.ParseHash(McpJson.String(args, "map")), mission = SolforgeTools.ParseHash(McpJson.String(args, "mission"));
                if (guarded && registry.MapGuardProblem(caller, presence, map, mission, McpJson.Bool(args, "force", false),
                        tool.Name == "open_map" ? "open" : "close") is { } problem)
                    return McpToolResult.Error(problem);
                var activity = tracked ? registry.Begin(caller, tool.Name, Summary(tool.Name, args), background) : null;
                McpToolResult result;
                try { result = await tool.Handler(args, cancel).ConfigureAwait(false); }
                catch (Exception)
                {
                    if (activity is not null) registry.Replied(activity, false, "failed");
                    throw;
                }
                string text = result.Texts.FirstOrDefault() ?? "";
                bool ok = !result.IsError && Succeeded(tool.Name, text);
                if (activity is not null) registry.Replied(activity, ok, text.Length > 200 ? text[..200] : text);
                if (ok && tool.Name == "open_map") registry.ClaimMap(caller, map ?? 0, mission);
                if (ok && tool.Name == "close_map") registry.ReleaseMap();
                return result;
            }
        };
    }

    /** Runs a map-scoped read against a background copy of the asked map; the UI's map is untouched. */
    private static async Task<McpToolResult> ScopedAsync(McpTool tool, JsonObject args, ISolforgePresence? presence, CancellationToken cancel)
    {
        if (presence is null) return McpToolResult.Error("This Solforge cannot read a second map in the background; leave map out.");
        if (SolforgeTools.ParseHash(McpJson.String(args, "map")) is not { } map)
            return McpToolResult.Error("map must be a map tag from list_maps, e.g. 0x80A1B2C3.");
        uint? mission = null;
        if (McpJson.String(args, "mission") is { Length: > 0 } missionText)
        {
            if (SolforgeTools.ParseHash(missionText) is not { } parsed) return McpToolResult.Error("mission must be a mission tag from list_missions.");
            mission = parsed;
        }
        var stripped = (JsonObject)args.DeepClone();
        stripped.Remove("map");
        stripped.Remove("mission");
        var scope = await presence.PrepareMapScopeAsync(map, mission, cancel).ConfigureAwait(false);
        if (scope.Error is { } error) return McpToolResult.Error(error);
        if (scope.Pending is { } pending)
            return McpToolResult.Error(pending + " Call again in a few seconds; the map open in Solforge's UI is unchanged.");
        if (scope.IsOpen) return await tool.Handler(stripped, cancel).ConfigureAwait(false);
        MapScope.Current = scope.View;
        try
        {
            var result = await tool.Handler(stripped, cancel).ConfigureAwait(false);
            result.Texts.Add($"Answered from a background copy of {scope.Name ?? $"0x{map:X8}"} (0x{map:X8}" +
                (mission is { } m ? $", mission 0x{m:X8}" : "") + "); the map open in Solforge's UI did not change.");
            return result;
        }
        finally { MapScope.Current = null; }
    }

    /** Action tools answer plain text even when they did nothing; these prefixes mean the work started. */
    private static bool Succeeded(string tool, string text) => tool switch
    {
        "open_map" => text.StartsWith("Opening", StringComparison.Ordinal),
        "close_map" => text.StartsWith("Closed", StringComparison.Ordinal),
        "build_project" or "build_to_folder" or "update_running_game" => text.StartsWith("Started", StringComparison.Ordinal),
        _ => true
    };

    /** A short line for the presence list: "Build and install", "Open map 0x80..". */
    private static string Summary(string tool, JsonObject args) => tool switch
    {
        "build_project" => McpJson.Bool(args, "install", false) ? "Build and install" : "Build",
        "build_to_folder" => "Build to " + (McpJson.String(args, "folder") ?? "a folder"),
        "update_running_game" => "Update running game",
        "open_map" => "Open " + (McpJson.String(args, "mission") is { Length: > 0 } m ? "mission " + m : "map " + McpJson.String(args, "map")),
        "local_server_deploy" => "Local server deploy",
        "local_server_rollback" => "Local server rollback",
        "local_server_restart" => "Local server restart",
        "local_server_start" => "Local server start",
        "local_server_stop" => "Local server stop",
        "local_server_selftest" => "Local server selftest",
        _ => tool
    };
}
