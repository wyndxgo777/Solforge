using System.Text.Json.Nodes;
using Solforge.Graphs;

namespace Solforge.Mcp;

/**
 * Node graph tools (material, weapon/item and mission graphs; *.solgraph.json in the project). Reads and edits work
 * on the files directly with Solforge.Graphs, so any MCP client drives them the same way; the Node Graph panel
 * reloads a graph it has open and unchanged. apply_graph hands the compiled result to the editor's existing
 * pipelines: item edits (apply_item_edit), material bundle import, or the project file write for mission Lua.
 */
public static partial class SolforgeTools
{
    public static readonly string[] GraphToolNames =
        ["list_graph_node_types", "list_graphs", "get_graph", "compile_graph", "create_graph", "edit_graph", "apply_graph"];

    private static string? GraphPath(ISolforgeHost host, JsonObject args, out string? reason)
    {
        string relative = McpJson.String(args, "path") ?? "";
        if (!relative.EndsWith(GraphDocument.Extension, StringComparison.OrdinalIgnoreCase))
        { reason = $"Graph paths end with {GraphDocument.Extension}, e.g. graphs/sol_fang{GraphDocument.Extension}."; return null; }
        return ProjectFiles.EditablePath(host.ProjectRoot, relative, out reason);
    }

    private static McpToolResult WithGraph(ISolforgeHost host, JsonObject args, Func<GraphDocument, McpToolResult> run)
    {
        if (GraphPath(host, args, out string? reason) is not { } path) return McpToolResult.Error(reason ?? "Refused.");
        if (!File.Exists(path)) return McpToolResult.Error("No such graph; list_graphs shows them, create_graph makes one.");
        try { return run(GraphDocument.Load(path)); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        { return McpToolResult.Error("Cannot read the graph: " + exception.Message); }
    }

    private static JsonObject OpsSchema()
    {
        var schema = McpJson.Schema(("path", "string", "Project-relative path ending .solgraph.json", true), ("ops", "array", "Edit ops (objects)", true));
        schema["properties"]!["ops"]!["items"] = new JsonObject { ["type"] = "object" };
        return schema;
    }

    private static IEnumerable<McpTool> GraphTools(ISolforgeHost host)
    {
        static JsonObject Schema(params (string, string, string, bool)[] properties) => McpJson.Schema(properties);
        McpTool Read(string name, string description, JsonObject schema, Func<JsonObject, McpToolResult> run) =>
            new(name, description, schema, false, (args, _) => Task.FromResult(run(args)));
        // Edits are approved like every action tool, then run here on the file.
        McpTool Act(string name, string description, JsonObject schema, Func<JsonObject, string?> validate,
            Func<JsonObject, string> summary, Func<JsonObject, CancellationToken, Task<McpToolResult>> run) =>
            new(name, description, schema, true, async (args, cancel) =>
            {
                if (validate(args) is { } problem) return McpToolResult.Error(problem);
                if (!await host.ApproveAsync(name, summary(args), cancel).ConfigureAwait(false))
                    return McpToolResult.Error("The user declined this in Solforge.");
                return await run(args, cancel).ConfigureAwait(false);
            });
        string? NeedPath(JsonObject args) => GraphPath(host, args, out string? reason) is null ? reason : null;

        yield return Read("list_graph_node_types", "Node types for a graph kind (material, weapon or mission) with pins (id, type), " +
            "properties (id, kind, default, choices) and descriptions. pin_type filters to types that can link to a dragged pin of that " +
            "type (from_output false when the dragged pin is an input); linkPin names the pin to connect.",
            Schema(("kind", "string", "material, weapon or mission", true), ("query", "string", "Optional words, e.g. 'lerp' or 'perk'", false),
                ("pin_type", "string", "Optional pin type: Exec, Bool, Int, Float, Vector2, Vector3, Color, Texture, String, Entity, Stat, Perk, Field", false),
                ("from_output", "boolean", "The dragged pin is an output (default true)", false)),
            args =>
            {
                var kind = GraphCommands.ParseKind(McpJson.String(args, "kind"));
                if (kind == GraphKind.None) return McpToolResult.Error("kind must be material, weapon or mission.");
                PinType? pin = Enum.TryParse<PinType>(McpJson.String(args, "pin_type") ?? "", true, out var parsed) ? parsed : null;
                return McpToolResult.Json(GraphCommands.NodeTypes(kind, McpJson.String(args, "query") ?? "", pin, McpJson.Bool(args, "from_output", true)));
            });
        yield return Read("list_graphs", "Graph files in the project (*.solgraph.json) with kind, name and node count.", Schema(),
            _ =>
            {
                if (host.ProjectRoot is not { } root || !Directory.Exists(root)) return McpToolResult.Error("No project is open.");
                var list = new JsonArray();
                foreach (string file in Directory.EnumerateFiles(root, "*" + GraphDocument.Extension, SearchOption.AllDirectories).Take(500))
                {
                    var row = new JsonObject { ["path"] = Path.GetRelativePath(root, file).Replace('\\', '/') };
                    try
                    {
                        var document = GraphDocument.Load(file);
                        row["kind"] = document.Kind.ToString().ToLowerInvariant(); row["name"] = document.Name; row["nodes"] = document.Nodes.Count;
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
                    { row["error"] = exception.Message; }
                    list.Add(row);
                }
                return McpToolResult.Json(list);
            });
        yield return Read("get_graph", "A graph: nodes (id, type, position, properties, resolved input and output pins), links, comments, " +
            "functions (collapsed subgraphs) and live diagnostics.",
            Schema(("path", "string", "Project-relative path ending .solgraph.json", true)),
            args => WithGraph(host, args, document => McpToolResult.Json(GraphCommands.Describe(document))));
        yield return Read("compile_graph", "Compile without applying: a material graph gives native constant and texture slot writes plus " +
            "the preview; a weapon graph gives apply_item_edit ops, custom perk rules and perkDefinitions (each perk's native sandbox program); a mission graph gives Lua. ok is false with errors.",
            Schema(("path", "string", "Project-relative path ending .solgraph.json", true)),
            args => WithGraph(host, args, document => McpToolResult.Json(GraphCommands.Compile(document))));

        yield return Act("create_graph", "Create a graph file with its output node (Material Output, Weapon Output or Mission Start). " +
            "Then add nodes with edit_graph (list_graph_node_types for types).",
            Schema(("path", "string", "Project-relative path ending .solgraph.json", true), ("kind", "string", "material, weapon or mission", true),
                ("name", "string", "Display name", false), ("target", "string", "Material or item tag (0x...) the graph edits", false)),
            args => NeedPath(args) ?? (GraphCommands.ParseKind(McpJson.String(args, "kind")) == GraphKind.None ? "kind must be material, weapon or mission." : null),
            args => $"Create {McpJson.String(args, "kind")} graph {McpJson.String(args, "path")}",
            (args, _) =>
            {
                string path = GraphPath(host, args, out string? ignored)!;
                if (File.Exists(path)) return Task.FromResult(McpToolResult.Error("That graph exists; use edit_graph."));
                var kind = GraphCommands.ParseKind(McpJson.String(args, "kind"));
                var document = GraphDocument.Create(kind, McpJson.String(args, "name") ?? Path.GetFileName(path)[..^GraphDocument.Extension.Length],
                    McpJson.String(args, "target") ?? "");
                document.Save(path);
                return Task.FromResult(McpToolResult.Json(GraphCommands.Describe(document)));
            });
        yield return Act("edit_graph", "Apply edit ops to a graph in order, saved as one change. Ops: add_node {type, x, y, properties, ref}, " +
            "connect {from, fromPin, to, toPin} (mismatched data types get a conversion node; a single input drops its old link), " +
            "disconnect {from, fromPin, to, toPin}, remove {ids}, set_property {node, key, value} (an input pin's id sets its unlinked value), " +
            "move {node, x, y}, add_comment {text, nodes, x, y}, add_reroute {from, fromPin, to, toPin, x, y}, collapse {nodes, name} " +
            "(to a function), expand {node}, align {nodes, mode}, distribute {nodes, axis}, toggle_collapsed {nodes}, rename {name}. " +
            "Use \"$ref\" for a node added earlier in the same call. Returns ids, per-op errors and diagnostics.",
            OpsSchema(),
            args => NeedPath(args) ?? (args["ops"] is JsonArray { Count: > 0 } ? null : "Give ops: a non-empty array."),
            args => $"Edit graph {McpJson.String(args, "path")}: " + string.Join(", ", (args["ops"] as JsonArray ?? new JsonArray())
                .Select(op => (string?)op?["op"] ?? "?").GroupBy(op => op).Select(group => group.Count() > 1 ? $"{group.Key} x{group.Count()}" : group.Key)),
            (args, _) =>
            {
                string path = GraphPath(host, args, out string? ignored)!;
                if (!File.Exists(path)) return Task.FromResult(McpToolResult.Error("No such graph; create_graph first."));
                try
                {
                    var editor = new GraphEditor(GraphDocument.Load(path));
                    var result = GraphCommands.Apply(editor, (JsonArray)args["ops"]!);
                    if ((int?)result["applied"] > 0) editor.Document.Save(path);
                    result["diagnostics"] = GraphCommands.Diagnostics(GraphValidation.Validate(editor.Document));
                    return Task.FromResult(McpToolResult.Json(result));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
                { return Task.FromResult(McpToolResult.Error("Cannot edit the graph: " + exception.Message)); }
            });
        yield return Act("apply_graph", "Compile a graph and apply it through Solforge's pipelines: weapon graphs run their item edits on the " +
            "target item (open it with open_asset first) and install each Custom Perk as a new sandbox perk + plug in a weapon socket, material graphs import their constant and texture writes into the target " +
            "material, mission graphs write the Lua to script (default: next to the graph, .lua). Each shows in Project changes with undo.",
            Schema(("path", "string", "Project-relative path ending .solgraph.json", true),
                ("script", "string", "Mission graphs: project-relative .lua path to write", false)),
            args => NeedPath(args),
            args => $"Apply graph {McpJson.String(args, "path")}",
            async (args, cancel) =>
            {
                string path = GraphPath(host, args, out _)!;
                if (!File.Exists(path)) return McpToolResult.Error("No such graph.");
                GraphDocument document;
                try { document = GraphDocument.Load(path); }
                catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
                { return McpToolResult.Error("Cannot read the graph: " + exception.Message); }
                var compiled = GraphCommands.Compile(document);
                if ((bool?)compiled["ok"] != true) return McpToolResult.Json(compiled);
                if (document.Kind == GraphKind.Mission)
                {
                    string relative = McpJson.String(args, "script")
                        ?? (McpJson.String(args, "path") ?? "")[..^GraphDocument.Extension.Length] + ".lua";
                    var write = new JsonObject { ["path"] = relative, ["content"] = (string?)compiled["lua"] ?? "" };
                    return McpToolResult.Text(await host.RunActionAsync("apply_project_edit", write, cancel).ConfigureAwait(false));
                }
                return McpToolResult.Text(await host.RunActionAsync("apply_graph", new JsonObject { ["path"] = McpJson.String(args, "path") }, cancel)
                    .ConfigureAwait(false));
            });
    }
}
