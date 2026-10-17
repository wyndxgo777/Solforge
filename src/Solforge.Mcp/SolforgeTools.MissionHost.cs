/**
 * File: MCP check_mission_script and simulate_mission, the Graph panel's Check script and Simulate buttons for AI
 * clients. The editor runs them (ISolforgeHost.QueryAsync -> StudioForm.Ai.MissionHost.cs -> SunriseStudio.Missions
 * MissionHost) in a bundled Lua 5.4.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

public static partial class SolforgeTools
{
    public static readonly string[] MissionHostToolNames = ["check_mission_script", "simulate_mission"];

    /** Yields the mission script check and simulator tools. */
    private static IEnumerable<McpTool> MissionHostTools(ISolforgeHost host,
        Func<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>, McpTool> read)
    {
        McpTool Query(string name, string description, JsonObject schema) =>
            read(name, description, schema, async (args, cancel) =>
            {
                var result = await host.QueryAsync(name, args, cancel).ConfigureAwait(false);
                return result["error"] is JsonValue error ? McpToolResult.Error(error.ToString()) : McpToolResult.Json(result);
            });
        const string PathHelp = "Project path (scripts/mission_x/mission_x.lua), scripts:<path> as list_mission_scripts gives it, or a full " +
            "path inside the project, the server scripts folder or the installed scripts; empty = the script open in the Graph panel";

        yield return Query("check_mission_script", "Check a Lua mission script without the game (the Graph panel's Check script): " +
            "Lua 5.4 syntax; every key the script reads from the generated mission modules (mission.Slot.X, Squad, Scene, " +
            "DialogueCue...) and every hash or slot/squad/scene id literal it writes must exist in the game data; with a map open " +
            "on the same mission, each Slot constant must also be one of its objects (the list_mission_objects lookup); and the " +
            "script library's own content checks (lib/campaign.lua check(), lib.one) must pass. Problems come back with line numbers.",
            McpJson.Schema(("path", "string", PathHelp, false)));
        yield return Query("simulate_mission", "Walk a campaign mission script (lib/campaign.lua) step by step through a fake game " +
            "host (the Graph panel's Simulate). Each step's own end is fired in turn. Modes: play (cutscenes play), skip (cutscenes " +
            "are skipped), cutscene_never_loads (the outro cutscene region never loads; pending timers fire), rejoin (the player " +
            "leaves and reattaches mid-mission, and again after the end). Returns the step trace, stalls (a step that never ends, " +
            "no completion, or more than one), script errors with line numbers, and mocks: host calls the fake host does not model.",
            McpJson.Schema(("path", "string", PathHelp, false),
                ("mode", "string", "play, skip, cutscene_never_loads or rejoin (default play)", false),
                ("trace", "boolean", "Include the step trace (default true)", false)));
    }
}
