/**
 * File: the get_client_build MCP tool, which says which source tree built the installed client DLL.
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp.ClientProvenance;

/** Read-only MCP tool reporting the installed client's build number, timestamp and matching source tree. */
public static class ClientBuildTools
{
    public const string Instructions =
        " get_client_build says which source tree built the installed client DLL (build number, timestamp, matching tree with git branch, HEAD and dirty state).";

    public static readonly string[] Names = ["get_client_build"];

    /** Builds the tool list; dll and trees default to the installed sol-client2 and the known source trees. */
    public static IReadOnlyList<McpTool> Create() =>
    [
        new McpTool("get_client_build",
            "Which source tree built the installed client DLL (steam_api64.dll)? Returns its version resource build number, file timestamp, " +
            "and the best matching tree (sol-0.6, Sunrise or sol-server-src) with git branch, short HEAD, dirty file count and how many " +
            "changed after the build. Matching: embedded tree/git (once the client writes it), byte-identical build output, same size and " +
            "time, then build_number.txt. confidence is exact, likely, weak or none. Reads only.",
            McpJson.Schema(("dll", "string", "Path to the DLL; default is the sol-client2 install (SOL_CLIENT_DIR)", false)),
            false,
            (args, _) => Task.Run(() =>
            {
                string dll = McpJson.String(args, "dll") is { Length: > 0 } given ? given : ClientBuildInfo.DefaultDll();
                return McpToolResult.Json(ClientBuildInfo.Inspect(dll, ClientBuildInfo.DefaultTrees()).ToJson());
            })),
    ];
}
