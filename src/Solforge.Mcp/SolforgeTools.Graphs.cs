/**
 * File: MCP graph tools (list_graph_node_types, list_graphs, get_graph, compile_graph, create_graph, edit_graph,
 * apply_graph) over Solforge.Graphs; graphs are project files with the GraphDocument extension.
 */
using System.Text.Json.Nodes;
using Solforge.Graphs;

namespace Solforge.Mcp;

/**
 * Node graph tools (material, weapon/item and mission graphs; *.solgraph.json in the project). Reads and edits work
 * on the files directly with Solforge.Graphs, so any MCP client drives them the same way; the Graph panel's node model
 * reloads a graph it has open and unchanged. apply_graph hands the compiled result to the editor's existing
 * pipelines: item edits (apply_item_edit), material bundle import, or the project file write for mission Lua.
 */
public static partial class SolforgeTools
{
    public static readonly string[] GraphToolNames =
        ["list_graph_node_types", "list_graphs", "get_graph", "compile_graph", "create_graph", "edit_graph", "apply_graph", "create_mission_script",
         "list_mission_lib_calls", "insert_mission_lib_call"];

    /** create_mission_script's activity ids, also accepted by create_graph(kind=mission, activity=...). */
    private const string ActivityIds = "story, strike, patrol, public_event, lost_sector, raid, encounter, pvp, quest or custom";

    /** Resolves the graph file path argument inside the project; on failure returns null and sets reason. */
    private static string? GraphPath(ISolforgeHost host, JsonObject args, out string? reason)
    {
        string relative = McpJson.String(args, "path") ?? "";
        if (!relative.EndsWith(GraphDocument.Extension, StringComparison.OrdinalIgnoreCase))
        { reason = $"Graph paths end with {GraphDocument.Extension}, e.g. graphs/sol_fang{GraphDocument.Extension}."; return null; }
        return ProjectFiles.EditablePath(host.ProjectRoot, relative, out reason);
    }

    /**
     * Loads the graph named by the arguments and runs the callback on it, returning tool errors for missing or bad
     * files.
     */
    private static McpToolResult WithGraph(ISolforgeHost host, JsonObject args, Func<GraphDocument, McpToolResult> run)
    {
        if (GraphPath(host, args, out string? reason) is not { } path) return McpToolResult.Error(reason ?? "Refused.");
        if (!File.Exists(path)) return McpToolResult.Error("No such graph; list_graphs shows them, create_graph makes one.");
        try { return run(GraphDocument.Load(path)); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        { return McpToolResult.Error("Cannot read the graph: " + exception.Message); }
    }

    /** Schema of edit_graph: path plus an ops array. */
    private static JsonObject OpsSchema()
    {
        var schema = McpJson.Schema(("path", "string", "Project-relative path ending .solgraph.json", true), ("ops", "array", "Edit ops (objects)", true));
        schema["properties"]!["ops"]!["items"] = new JsonObject { ["type"] = "object" };
        return schema;
    }

    /** Yields the graph tools. */
    private static IEnumerable<McpTool> GraphTools(ISolforgeHost host)
    {
        /** Shorthand for a JSON schema. */
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

        yield return Act("create_mission_script", "Create a new server mission script for any activity type from its template, saved " +
            "into the project's scripts (it shows in Project changes and the build) and opened in the Graph panel (graph_kind mission_script): " +
            "nodes for campaign steps and encounters, the Lua, and Checks that list CHANGE_ME placeholders and Slot/Squad/Directive names the " +
            "map's mission does not have. activity: " + ActivityIds + ". Story, strike, raid, encounter and quest use lib.campaign; patrol, " +
            "public_event and lost_sector use lib.freeroam; pvp uses lib.mission_lib timers; custom is the bare runtime table.",
            Schema(("activity", "string", ActivityIds, true), ("mission", "string", "missions.lua key (e.g. STRIKE_HYMN) or the map name; its SDK names fill the template", true),
                ("name", "string", "Script name; the file is scripts/<stem>/<stem>.lua", true),
                ("in_project", "boolean", "Save into the project (default true); false writes the installed scripts", false)),
            args => McpJson.String(args, "activity") is null || McpJson.String(args, "mission") is null || McpJson.String(args, "name") is null
                ? "Give activity, mission and name." : null,
            args => $"Create a {McpJson.String(args, "activity")} mission script {McpJson.String(args, "name")} for {McpJson.String(args, "mission")}",
            async (args, cancel) => McpToolResult.Text(await host.RunActionAsync("create_mission_script", args, cancel).ConfigureAwait(false)));

        yield return Act("create_graph", "Create a graph file with its output node (Material Output, Weapon Output or Mission Start). " +
            "Then add nodes with edit_graph (list_graph_node_types for types). With kind mission and an activity (" + ActivityIds + ") and " +
            "mission/map it creates a server mission script from that activity's template instead (as create_mission_script; no path needed).",
            Schema(("path", "string", "Project-relative path ending .solgraph.json (not needed with activity)", false), ("kind", "string", "material, weapon or mission", true),
                ("name", "string", "Display name", false), ("target", "string", "Material or item tag (0x...) the graph edits", false),
                ("activity", "string", "Mission kind only: " + ActivityIds, false),
                ("map", "string", "With activity: the missions.lua key or map name (also accepted as 'mission')", false),
                ("template", "string", "Mission node graphs: a graph template from list_mission_lib_calls (cutscene_sky_battle, " +
                    "cutscene_light_restored, cutscene_boss_intro, fade_area_move, boss_pylons)", false)),
            args => McpJson.String(args, "activity") is not null
                ? GraphCommands.ParseKind(McpJson.String(args, "kind")) != GraphKind.Mission ? "activity needs kind mission."
                    : (McpJson.String(args, "map") ?? McpJson.String(args, "mission")) is null ? "Give map (the mission key or map name) with activity." : null
                : NeedPath(args) ?? (GraphCommands.ParseKind(McpJson.String(args, "kind")) == GraphKind.None ? "kind must be material, weapon or mission." : null),
            args => McpJson.String(args, "activity") is { } activity
                ? $"Create a {activity} mission script for {McpJson.String(args, "map") ?? McpJson.String(args, "mission")}"
                : $"Create {McpJson.String(args, "kind")} graph {McpJson.String(args, "path")}",
            async (args, cancel) =>
            {
                if (McpJson.String(args, "activity") is { } activity)
                {
                    string mission = McpJson.String(args, "map") ?? McpJson.String(args, "mission")!;
                    var create = new JsonObject
                    {
                        ["activity"] = activity, ["mission"] = mission,
                        ["name"] = McpJson.String(args, "name") ?? mission.ToLowerInvariant() + "_" + activity.ToLowerInvariant(),
                        ["in_project"] = true
                    };
                    return McpToolResult.Text(await host.RunActionAsync("create_mission_script", create, cancel).ConfigureAwait(false));
                }
                return CreateNodeGraph(args);
            });
        McpToolResult CreateNodeGraph(JsonObject args)
            {
                string path = GraphPath(host, args, out string? ignored)!;
                if (File.Exists(path)) return McpToolResult.Error("That graph exists; use edit_graph.");
                var kind = GraphCommands.ParseKind(McpJson.String(args, "kind"));
                string name = McpJson.String(args, "name") ?? Path.GetFileName(path)[..^GraphDocument.Extension.Length];
                GraphDocument document;
                if (McpJson.String(args, "template") is { } template)
                {
                    if (kind != GraphKind.Mission) return McpToolResult.Error("Graph templates are mission graphs; use kind mission.");
                    try { document = MissionGraphTemplates.Build(template, name); }
                    catch (InvalidDataException exception) { return McpToolResult.Error(exception.Message); }
                }
                else document = GraphDocument.Create(kind, name, McpJson.String(args, "target") ?? "");
                document.Save(path);
                return McpToolResult.Json(GraphCommands.Describe(document));
            }
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

        yield return Read("list_mission_lib_calls", "Mission-library calls (lib.mission_lib) for content: fade/area transitions with " +
            "teleport, mid-mission cutscenes, boss shield/pylon/invulnerability phases and ordered endings. Each has its Lua function, graph node " +
            "type (lib.*), fields (name, kind, default, required, help) and pending (true while the name is a placeholder for the server's " +
            "mission-lib-transitions branch). Also lists the mission graph templates (create_graph kind=mission template=...) built on them. " +
            "With call and values it renders the Lua line and its problems.",
            Schema(("call", "string", "Optional function, e.g. transition, play_cutscene, boss_phase, set_invulnerable, break_shield, ending", false),
                ("values", "object", "Optional field values to render with call", false)),
            args =>
            {
                if (McpJson.String(args, "call") is { } name)
                {
                    if (MissionLibrary.Find(name) is not { } one) return McpToolResult.Error("No such call; omit call to list them.");
                    var values = LibValues(args);
                    return McpToolResult.Json(new JsonObject
                    {
                        ["lua"] = MissionLibrary.Render(one, values), ["require"] = MissionLibrary.Require,
                        ["problems"] = new JsonArray(MissionLibrary.Check(one, values).Select(problem => (JsonNode)problem).ToArray())
                    });
                }
                var calls = new JsonArray();
                foreach (var call in MissionLibrary.Calls)
                    calls.Add(new JsonObject
                    {
                        ["function"] = call.Function, ["title"] = call.Title, ["feature"] = call.Feature, ["node_type"] = call.NodeType,
                        ["description"] = call.Description, ["pending"] = call.Pending,
                        ["example"] = MissionLibrary.Render(call, call.Parameters.Where(field => field.Required)
                            .ToDictionary(field => field.Name, _ => "Slot.CHANGE_ME", StringComparer.Ordinal)),
                        ["fields"] = new JsonArray(call.Parameters.Select(field => (JsonNode)new JsonObject
                        {
                            ["name"] = field.Name, ["label"] = field.Label, ["kind"] = field.Kind.ToString().ToLowerInvariant(),
                            ["default"] = field.Default, ["required"] = field.Required, ["help"] = field.Help,
                            ["choices"] = field.Choices is null ? null : new JsonArray(field.Choices.Select(choice => (JsonNode)choice).ToArray())
                        }).ToArray())
                    });
                var templates = new JsonArray(MissionGraphTemplates.All.Select(template => (JsonNode)new JsonObject
                {
                    ["id"] = template.Id, ["title"] = template.Title, ["examples"] = template.Examples, ["description"] = template.Description
                }).ToArray());
                return McpToolResult.Json(new JsonObject
                {
                    ["module"] = MissionLibrary.Module, ["require"] = MissionLibrary.Require, ["calls"] = calls, ["graph_templates"] = templates
                });
            });

        yield return Act("insert_mission_lib_call", "Insert a mission-library call (list_mission_lib_calls) into content. A .solgraph.json mission " +
            "graph gets the lib.* node, linked after the node 'after' (its Then; what followed now follows the new node). A .lua mission script " +
            "gets the Lua line as the first statement of 'function' (a callback such as on_start, or a named function) plus the require " +
            "line, through Project changes with undo.",
            Schema(("path", "string", "Project-relative .solgraph.json or .lua path", true), ("call", "string", "Library function, e.g. transition", true),
                ("values", "object", "Field values (Lua expressions for slots: Slot.X)", false),
                ("after", "string", "Graphs: node id whose Then runs the new node", false),
                ("function", "string", "Scripts: function or callback name to insert into", false)),
            args =>
            {
                if (MissionLibrary.Find(McpJson.String(args, "call") ?? "") is null) return "Unknown call; see list_mission_lib_calls.";
                string path = McpJson.String(args, "path") ?? "";
                if (!path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) return NeedPath(args);
                if (McpJson.String(args, "function") is null) return "Give function (the callback or function to insert into).";
                return ProjectFiles.EditablePath(host.ProjectRoot, path, out string? reason) is null ? reason ?? "Refused." : null;
            },
            args => $"Insert {McpJson.String(args, "call")} into {McpJson.String(args, "path")}",
            async (args, cancel) =>
            {
                var call = MissionLibrary.Find(McpJson.String(args, "call")!)!;
                var values = LibValues(args);
                string relative = McpJson.String(args, "path")!;
                try
                {
                    if (relative.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                    {
                        string file = ProjectFiles.EditablePath(host.ProjectRoot, relative, out _)!;
                        if (!File.Exists(file)) return McpToolResult.Error("No such script in the project; create_mission_script makes one.");
                        string updated = MissionLibrary.InsertIntoLua(await File.ReadAllTextAsync(file, cancel).ConfigureAwait(false),
                            McpJson.String(args, "function")!, MissionLibrary.Render(call, values));
                        var write = new JsonObject { ["path"] = relative, ["content"] = updated };
                        return McpToolResult.Text(await host.RunActionAsync("apply_project_edit", write, cancel).ConfigureAwait(false));
                    }
                    string path = GraphPath(host, args, out _)!;
                    if (!File.Exists(path)) return McpToolResult.Error("No such graph; create_graph first.");
                    var editor = new GraphEditor(GraphDocument.Load(path));
                    if (editor.Document.Kind != GraphKind.Mission) return McpToolResult.Error("Library calls go in mission graphs.");
                    string? after = McpJson.String(args, "after");
                    var previous = after is null ? null : editor.Node(after);
                    if (after is not null && previous is null) return McpToolResult.Error("No node " + after + " in the graph.");
                    var node = editor.AddNode(call.NodeType, (previous?.X ?? 0) + 260, (previous?.Y ?? 0) + 120, values);
                    if (previous is not null)
                    {
                        var next = editor.Links.Where(link => link.FromNode == previous.Id && link.FromPin == "then").ToList();
                        foreach (var link in next) editor.Disconnect(link);
                        if (editor.Connect(previous.Id, "then", node.Id, "exec") is { } problem) return McpToolResult.Error(problem);
                        foreach (var link in next) editor.Connect(node.Id, "then", link.ToNode, link.ToPin);
                    }
                    editor.Document.Save(path);
                    return McpToolResult.Json(new JsonObject
                    {
                        ["node"] = node.Id, ["diagnostics"] = GraphCommands.Diagnostics(GraphValidation.Validate(editor.Document))
                    });
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
                { return McpToolResult.Error("Cannot insert: " + exception.Message); }
            });
    }

    /** The values argument as field strings (booleans and numbers as their Lua text). */
    private static Dictionary<string, string> LibValues(JsonObject args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args["values"] is not JsonObject table) return values;
        foreach (var (key, value) in table)
            values[key] = value is JsonValue scalar && scalar.TryGetValue<bool>(out bool flag) ? (flag ? "true" : "false")
                : value is JsonValue text && text.TryGetValue<string>(out string? written) ? written : value?.ToJsonString() ?? "";
        return values;
    }
}
