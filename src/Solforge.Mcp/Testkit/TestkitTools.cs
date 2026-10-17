/**
 * File: MCP tools testkit_status (read) and testkit_restart (asks for approval) for the sol-testkit MCP.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp.Testkit;

/** Model-agnostic MCP tools for the sol-testkit health light and restart. */
public static class TestkitTools
{
    public const string Instructions =
        " testkit_status checks the sol-testkit MCP (process, ports, handshake) and gives a green, amber or red light; testkit_restart ends it so the client " +
        "reconnects, or starts its HTTP mode.";

    public static readonly string[] Names = ["testkit_status", "testkit_restart"];

    /** Builds both tools; approve asks the user to confirm the restart. */
    public static IReadOnlyList<McpTool> Create(TestkitMonitor monitor, Func<string, string, CancellationToken, Task<bool>> approve) =>
    [
        new McpTool("testkit_status",
            "Health of the sol-testkit MCP (screen/pad/log tools): light green, amber or red, running instances, pad and live capture ports, a " +
            "throwaway MCP handshake with tool count, and how it is launched (stdio from .mcp.json, owned by the client). Reads only.",
            McpJson.Schema(), false,
            async (_, cancel) => McpToolResult.Json((await monitor.StatusAsync(cancel).ConfigureAwait(false)).ToJson())),
        new McpTool("testkit_restart",
            "Restart the sol-testkit MCP. mode 'kill' (default) ends the running testkit; the client (Claude Code) respawns it when you reconnect " +
            "with /mcp, and tools like capture and pad fail until then. mode 'http' also starts the script's HTTP server on port 48840. " +
            "Asks the user to approve first; this cuts the testkit tools of the current session.",
            McpJson.Schema(("mode", "string", "kill (default) or http", false)), true,
            async (args, cancel) =>
            {
                string mode = McpJson.String(args, "mode") is { Length: > 0 } given ? given.ToLowerInvariant() : "kill";
                if (mode is not ("kill" or "http")) return McpToolResult.Error("mode must be kill or http");
                if (!await approve("testkit_restart", "Restart the sol-testkit MCP (" + mode + "); its tools drop until the client reconnects.", cancel).ConfigureAwait(false))
                    return McpToolResult.Error("The user declined this in Solforge.");
                var result = await monitor.RestartAsync(mode, cancel).ConfigureAwait(false);
                return result.Ok ? McpToolResult.Json(result.ToJson()) : McpToolResult.Error(result.ToJson().ToJsonString(McpJson.Indented));
            }),
    ];
}
