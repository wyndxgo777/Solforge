/** File: MCP tools get_player_position and wait_for_player_near; sources are combined in PlayerTracking.cs. */
using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** Live player position: the host merges the Live link, the test recorder and the local server; these tools read it. */
public static partial class SolforgeTools
{
    /** Yields the player tracking tools. */
    private static IEnumerable<McpTool> PlayerTools(ISolforgeHost host,
        Func<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>, McpTool> read)
    {
        yield return read("get_player_position", "Where the player is in game right now: source (live link, recorder or local " +
            "server), age, map and zone (bubble), activity, position xyz in map space, yaw in degrees (atan2 of the facing, " +
            "0 = +X, 90 = +Y), the nearest named objects and mission slots with distances, and whether the open map is the " +
            "player's. Give target to also get that object's position and its distance from the player.",
            McpJson.Schema(("target", "string", "Optional object id, mission slot id (slot/...) or name", false),
                ("near", "integer", "How many nearest objects and slots (default 5, max 25)", false)),
            async (args, cancel) => Reply(await host.QueryAsync("get_player_position", args, cancel).ConfigureAwait(false)));

        yield return read("wait_for_player_near", "Wait until the player is within radius metres of an object (target: id, slot id " +
            "or name) or a point (position [x,y,z]), up to timeout seconds. Returns reached, the distance, how long it waited and " +
            "the player's position; use it to wait for the user (or a test pad) to walk somewhere.",
            Vectors(McpJson.Schema(("target", "string", "Object id, mission slot id or name", false),
                ("position", "array", "Point [x, y, z] in map space, instead of target", false),
                ("radius", "number", "Metres (default 5)", false),
                ("horizontal", "boolean", "Ignore height (default false)", false),
                ("timeout", "number", "Seconds to wait (default 30, max 120)", false))),
            async (args, cancel) =>
            {
                string? target = McpJson.String(args, "target");
                var point = PlayerSources.Vector(args["position"]);
                if (string.IsNullOrWhiteSpace(target) && point is null) return McpToolResult.Error("Give a target (object, slot or name) or a position [x, y, z].");
                float radius = (float)Math.Clamp(Seconds(args, "radius", 5), 0.1, 100000);
                var timeout = TimeSpan.FromSeconds(Math.Clamp(Seconds(args, "timeout", 30), 0, 120));
                bool horizontal = McpJson.Bool(args, "horizontal", false);
                var query = new JsonObject { ["near"] = 3 };
                if (point is null) query["target"] = target;
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    var state = await host.QueryAsync("get_player_position", (JsonObject)query.DeepClone(), cancel).ConfigureAwait(false);
                    if (state["error"] is JsonValue error) return McpToolResult.Error(error.ToString());
                    var goal = point ?? PlayerSources.Vector(state["target"]?["position"]);
                    if (goal is null) return McpToolResult.Error($"{target} has no position on this map.");
                    float? distance = PlayerSources.Vector(state["position"]) is { } at
                        ? horizontal ? Vector2.Distance(new(at.X, at.Y), new(goal.Value.X, goal.Value.Y)) : Vector3.Distance(at, goal.Value) : null;
                    bool reached = distance <= radius;
                    if (reached || clock.Elapsed >= timeout)
                        return McpToolResult.Json(new JsonObject
                        {
                            ["reached"] = reached, ["distance"] = distance is { } d ? Math.Round(d, 2) : null, ["radius"] = radius,
                            ["goal"] = PlayerSources.Json(goal.Value), ["waitedSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
                            ["player"] = state,
                            ["note"] = reached ? null : distance is null ? "No player position from any source (see player.sources)." : "Timed out."
                        });
                    await Task.Delay(250, cancel).ConfigureAwait(false);
                }
            });
    }

    /** Turns a host reply into a tool result (error text when it carries an error). */
    private static McpToolResult Reply(JsonObject result) =>
        result["error"] is JsonValue error ? McpToolResult.Error(error.ToString()) : McpToolResult.Json(result);

    /** Number argument or fallback. */
    private static double Seconds(JsonObject args, string name, double fallback) =>
        args[name] is JsonValue value && value.TryGetValue(out double number) && double.IsFinite(number) ? number : fallback;
}
