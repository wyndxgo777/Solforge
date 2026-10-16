/**
 * File: MCP tools list_shaders, get_shader, list_animations, get_animation, list_audio, get_audio (asset browsing
 * through ISolforgeHost).
 */
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * Read-only views of the installed game's shaders, animation clips and Wwise audio, through the editor's own package readers
 * (ISolforgeHost.QueryAsync). Metadata only: nothing is decoded here that the editor's renderer, animation or audio readers do
 * not already read. Like CatalogTools they need no map and keep working while context publishing is paused.
 */
public static partial class SolforgeTools
{
    /** Install-wide asset reads; they keep working while context publishing is paused. */
    public static readonly string[] AssetReadTools = ["list_shaders", "get_shader", "list_animations", "get_animation", "list_audio", "get_audio"];

    /** Yields the asset tools: shaders, animations and audio. */
    private static IEnumerable<McpTool> AssetTools(ISolforgeHost host,
        Func<string, string, JsonObject, Func<JsonObject, CancellationToken, Task<McpToolResult>>, McpTool> read)
    {
        McpTool Query(string name, string description, JsonObject schema, Func<JsonObject, string?>? validate = null) =>
            read(name, description, schema, async (args, cancel) =>
            {
                if (validate?.Invoke(args) is { } problem) return McpToolResult.Error(problem);
                var result = await host.QueryAsync(name, args, cancel).ConfigureAwait(false);
                return result["error"] is JsonValue error ? McpToolResult.Error(error.ToString()) : McpToolResult.Json(result);
            });
        /** Error text when the tag argument is missing or not a hash; null when valid. */
        static string? NeedTag(JsonObject args, string what) =>
            ParseHash(McpJson.String(args, "tag")) is null ? $"Give a {what} tag such as 0x80A1B2C3." : null;

        yield return Query("list_shaders", "Installed shaders (package shader headers), paged: tag, stage (vertex, pixel, geometry, " +
            "hull, domain, compute), bytecode tag, package and size. No map needed. Read one with get_shader.",
            Paged(("stage", "string", "Optional stage: vertex, pixel, geometry, hull, domain or compute", false),
                ("package", "string", "Optional text the package name must contain", false),
                ("filter", "string", "Optional text the tag or name must contain", false)));
        yield return Query("get_shader", "One shader by its header tag, or every stage of a material by the material tag: profile " +
            "(e.g. ps_5_0), constant buffers (slot and size in float4 registers), declared texture and sampler slots, the materials " +
            "that use it (partial index), and for a material the bound textures (stage, slot, texture tag, size, format). " +
            "disassembly=true adds the DXBC disassembly a page of lines at a time.",
            McpJson.Schema(("tag", "string", "Shader header tag or material tag (0x...)", true),
                ("disassembly", "boolean", "Include the disassembly (default false)", false),
                ("offset", "integer", "First disassembly line (default 0)", false),
                ("limit", "integer", "Disassembly lines per page (default 200, max 2000)", false)),
            args => NeedTag(args, "shader or material"));
        yield return Query("list_animations", "Animation clips, paged: tag, name, package. With entity (an entity tag), only that " +
            "entity's clips with their definition and clip index. No map needed. Read one with get_animation.",
            Paged(("filter", "string", "Optional words the clip name, tag or package must contain", false),
                ("entity", "string", "Optional entity tag (0x...) whose clips to list", false)));
        yield return Query("get_animation", "One animation clip: frame count, sample rate, length in seconds, payload sections, the " +
            "decoded control tracks (count, groups, and per track its key counts, paged) when the clip's codec is supported, and the " +
            "animation definitions that list it (users). With entity, or for an entity tag, also its skeleton (bones with name and parent).",
            McpJson.Schema(("tag", "string", "Clip tag (0x...), or an entity tag for its skeleton and clips", true),
                ("entity", "string", "Optional owning entity tag for the skeleton", false),
                ("offset", "integer", "First track or bone row (default 0)", false),
                ("limit", "integer", "Track or bone rows per page (default 50, max 500)", false)),
            args => NeedTag(args, "clip or entity"));
        yield return Query("list_audio", "Wwise audio in the install, paged: events, banks and streams with tag, kind, name, package " +
            "and size. No map needed. Read one with get_audio.",
            Paged(("kind", "string", "Optional: event, bank or stream", false),
                ("filter", "string", "Optional words the name, tag or package must contain", false)));
        yield return Query("get_audio", "One audio event, bank or stream: Wwise event id, related event id, linked bank (id, Wwise " +
            "version, object counts by HIRC type), media (id, stream tag, name, size, embedded or streamed) and diagnostics. " +
            "Metadata only; no audio is decoded.",
            McpJson.Schema(("tag", "string", "Event, bank or stream tag (0x...)", true)),
            args => NeedTag(args, "audio event, bank or stream"));
    }
}
