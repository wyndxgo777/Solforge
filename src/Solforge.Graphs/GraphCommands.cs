using System.Globalization;
using System.Text.Json.Nodes;

namespace Solforge.Graphs;

/**
 * Model-agnostic graph access as JSON, shared by the MCP tools and any script: describe a graph, apply a batch of
 * edit ops, compile it, and list node types. Every op goes through GraphEditor, so the editor's rules (type checks,
 * conversion nodes, single-input replacement) hold for AI edits too.
 */
public static class GraphCommands
{
    public static readonly string[] Ops =
    [
        "add_node", "connect", "disconnect", "remove", "set_property", "move", "add_comment", "add_reroute", "collapse",
        "expand", "align", "distribute", "toggle_collapsed", "rename"
    ];

    public static GraphKind ParseKind(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "material" => GraphKind.Material,
        "weapon" or "item" => GraphKind.Weapon,
        "mission" => GraphKind.Mission,
        _ => GraphKind.None
    };

    /** The graph as an agent reads it: nodes with resolved pins and values, links, comments, functions and diagnostics. */
    public static JsonObject Describe(GraphDocument document)
    {
        JsonArray Nodes(List<GraphNode> nodes, GraphFunction? scope) => new(nodes.Select(node =>
        {
            var type = NodeLibrary.Find(node.Type);
            var properties = new JsonObject();
            foreach (var (key, value) in node.Properties) properties[key] = value;
            return (JsonNode)new JsonObject
            {
                ["id"] = node.Id, ["type"] = node.Type, ["title"] = type?.Title ?? node.Type, ["x"] = node.X, ["y"] = node.Y,
                ["collapsed"] = node.Collapsed, ["properties"] = properties,
                ["inputs"] = new JsonArray(document.PinsOf(node, scope).Where(pin => !pin.Output).Select(PinJson).ToArray()),
                ["outputs"] = new JsonArray(document.PinsOf(node, scope).Where(pin => pin.Output).Select(PinJson).ToArray())
            };
        }).ToArray());
        static JsonArray Links(List<GraphLink> links) => new(links.Select(link => (JsonNode)new JsonObject
            { ["from"] = link.FromNode, ["fromPin"] = link.FromPin, ["to"] = link.ToNode, ["toPin"] = link.ToPin }).ToArray());
        return new JsonObject
        {
            ["name"] = document.Name, ["kind"] = document.Kind.ToString().ToLowerInvariant(),
            ["nodes"] = Nodes(document.Nodes, null), ["links"] = Links(document.Links),
            ["comments"] = new JsonArray(document.Comments.Select(comment => (JsonNode)new JsonObject
            {
                ["id"] = comment.Id, ["text"] = comment.Text, ["x"] = comment.X, ["y"] = comment.Y, ["width"] = comment.Width, ["height"] = comment.Height
            }).ToArray()),
            ["functions"] = new JsonArray(document.Functions.Select(function => (JsonNode)new JsonObject
            {
                ["id"] = function.Id, ["name"] = function.Name, ["exec"] = function.Exec,
                ["inputs"] = new JsonArray(function.Inputs.Select(pin => (JsonNode)new JsonObject { ["id"] = pin.Id, ["name"] = pin.Name, ["type"] = pin.Type.ToString() }).ToArray()),
                ["outputs"] = new JsonArray(function.Outputs.Select(pin => (JsonNode)new JsonObject { ["id"] = pin.Id, ["name"] = pin.Name, ["type"] = pin.Type.ToString() }).ToArray()),
                ["nodes"] = Nodes(function.Nodes, function), ["links"] = Links(function.Links)
            }).ToArray()),
            ["diagnostics"] = Diagnostics(GraphValidation.Validate(document))
        };
    }

    private static JsonNode PinJson(PinDef pin)
    {
        var json = new JsonObject { ["id"] = pin.Id, ["name"] = pin.Name, ["type"] = pin.Type.ToString() };
        if (pin.Default is not null) json["default"] = pin.Default;
        if (pin.Multi) json["multi"] = true;
        if (pin.Required) json["required"] = true;
        return json;
    }

    public static JsonArray Diagnostics(IEnumerable<GraphDiagnostic> diagnostics) => new(diagnostics.Select(item =>
    {
        var json = new JsonObject { ["severity"] = item.Severity.ToString().ToLowerInvariant(), ["message"] = item.Message };
        if (item.NodeId is not null) json["node"] = item.NodeId;
        if (item.Scope is not null) json["function"] = item.Scope;
        return (JsonNode)json;
    }).ToArray());

    /** Node types for a graph kind, optionally filtered by words and by a pin type that must link. */
    public static JsonArray NodeTypes(GraphKind kind, string query, PinType? pinType, bool fromOutput) =>
        new(NodeLibrary.Search(kind, query, pinType, fromOutput).Select(result => (JsonNode)new JsonObject
        {
            ["type"] = result.Type.Id, ["title"] = result.Type.Title, ["category"] = result.Type.Category,
            ["description"] = result.Type.Description,
            ["inputs"] = new JsonArray(result.Type.Inputs.Select(PinJson).ToArray()),
            ["outputs"] = new JsonArray(result.Type.Outputs.Select(PinJson).ToArray()),
            ["properties"] = new JsonArray(result.Type.Properties.Select(property =>
            {
                var json = new JsonObject { ["id"] = property.Id, ["name"] = property.Name, ["kind"] = property.Kind.ToString(), ["default"] = property.Default };
                if (property.Choices is { } choices) json["choices"] = new JsonArray(choices.Select(choice => (JsonNode)JsonValue.Create(choice)!).ToArray());
                if (property.Help.Length > 0) json["help"] = property.Help;
                return (JsonNode)json;
            }).ToArray()),
            ["linkPin"] = result.PinId
        }).ToArray());

    /** Compiles by kind: material params, item edit ops (+ custom perk rules) or Lua. */
    public static JsonObject Compile(GraphDocument document)
    {
        switch (document.Kind)
        {
            case GraphKind.Material:
            {
                var result = MaterialGraphCompiler.Compile(document);
                var json = MaterialGraphCompiler.ToJson(result);
                json["diagnostics"] = Diagnostics(result.Diagnostics);
                json["ok"] = !GraphValidation.HasErrors(result.Diagnostics);
                return json;
            }
            case GraphKind.Weapon:
            {
                var result = WeaponGraphCompiler.Compile(document);
                var json = WeaponGraphCompiler.ToJson(result);
                json["diagnostics"] = Diagnostics(result.Diagnostics);
                json["ok"] = !GraphValidation.HasErrors(result.Diagnostics) && result.Target.Length > 0;
                return json;
            }
            case GraphKind.Mission:
            {
                var result = MissionGraphCompiler.Compile(document);
                return new JsonObject
                {
                    ["lua"] = result.Lua, ["handlers"] = new JsonArray(result.Handlers.Select(name => (JsonNode)JsonValue.Create(name)!).ToArray()),
                    ["diagnostics"] = Diagnostics(result.Diagnostics), ["ok"] = !GraphValidation.HasErrors(result.Diagnostics)
                };
            }
            default:
                return new JsonObject { ["ok"] = false, ["diagnostics"] = Diagnostics([new(DiagnosticSeverity.Error, "Unknown graph kind.")]) };
        }
    }

    /**
     * Applies edit ops in order. An add_node with "ref" names its new node so later ops can use "$ref" in place of an id.
     * Ops after a failing op still run; the result lists each op's error. Returns {applied, ids, errors}.
     */
    public static JsonObject Apply(GraphEditor editor, JsonArray ops)
    {
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new JsonArray();
        int applied = 0;
        string Id(JsonObject op, string name)
        {
            string text = (string?)op[name] ?? "";
            return text.StartsWith('$') && refs.TryGetValue(text[1..], out var id) ? id : text;
        }
        static float Number(JsonObject op, string name, float fallback) =>
            op[name] is JsonValue value && value.TryGetValue(out double number) ? (float)number
                : op[name] is JsonValue text && text.TryGetValue(out string? raw) && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : fallback;
        List<string> Ids(JsonObject op, string name) => op[name] is JsonArray array
            ? array.Select(item => (string?)item ?? "").Select(text => text.StartsWith('$') && refs.TryGetValue(text[1..], out var id) ? id : text).ToList()
            : [];

        for (int index = 0; index < ops.Count; index++)
        {
            if (ops[index] is not JsonObject op) { errors.Add($"op {index}: not an object"); continue; }
            string kind = (string?)op["op"] ?? "";
            try
            {
                string? problem = null;
                switch (kind)
                {
                    case "add_node":
                    {
                        string type = (string?)op["type"] ?? "";
                        if (NodeLibrary.Find(type) is not { } definition) { problem = $"unknown node type '{type}' (list_graph_node_types)"; break; }
                        if ((definition.Kinds & editor.Document.Kind) == 0) { problem = $"{type} is not a {editor.Document.Kind} node"; break; }
                        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
                        if (op["properties"] is JsonObject values)
                            foreach (var (key, value) in values) properties[key] = value is JsonValue scalar && scalar.TryGetValue(out string? s) ? s : value?.ToJsonString() ?? "";
                        var auto = editor.Nodes.Count == 0 ? (0f, 0f) : (editor.Nodes.Max(node => node.X) + 300, editor.Nodes.Min(node => node.Y));
                        var node = editor.AddNode(type, Number(op, "x", auto.Item1), Number(op, "y", auto.Item2), properties);
                        if ((string?)op["ref"] is { Length: > 0 } name) refs[name] = node.Id;
                        break;
                    }
                    case "connect":
                        problem = editor.Connect(Id(op, "from"), (string?)op["fromPin"] ?? "", Id(op, "to"), (string?)op["toPin"] ?? "");
                        break;
                    case "disconnect":
                    {
                        var link = new GraphLink(Id(op, "from"), (string?)op["fromPin"] ?? "", Id(op, "to"), (string?)op["toPin"] ?? "");
                        if (!editor.Links.Contains(link)) problem = "no such link"; else editor.Disconnect(link);
                        break;
                    }
                    case "remove": editor.Remove(Ids(op, "ids")); break;
                    case "set_property":
                    {
                        if (editor.Node(Id(op, "node")) is null) { problem = "no such node"; break; }
                        var value = op["value"];
                        editor.SetProperty(Id(op, "node"), (string?)op["key"] ?? "",
                            value is JsonValue scalar && scalar.TryGetValue(out string? s) ? s : value?.ToJsonString() ?? "");
                        break;
                    }
                    case "move":
                    {
                        if (editor.Node(Id(op, "node")) is not { } node) { problem = "no such node"; break; }
                        editor.Checkpoint("Move");
                        editor.Move([node.Id], Number(op, "x", node.X) - node.X, Number(op, "y", node.Y) - node.Y);
                        break;
                    }
                    case "add_comment":
                    {
                        editor.Selection.Clear(); editor.Selection.UnionWith(Ids(op, "nodes"));
                        var comment = editor.AddComment(Number(op, "x", 0), Number(op, "y", 0), (string?)op["text"] ?? "Comment");
                        if (op["width"] is not null) comment.Width = Number(op, "width", comment.Width);
                        if (op["height"] is not null) comment.Height = Number(op, "height", comment.Height);
                        if ((string?)op["ref"] is { Length: > 0 } name) refs[name] = comment.Id;
                        break;
                    }
                    case "add_reroute":
                    {
                        var link = new GraphLink(Id(op, "from"), (string?)op["fromPin"] ?? "", Id(op, "to"), (string?)op["toPin"] ?? "");
                        if (!editor.Links.Contains(link)) { problem = "no such link"; break; }
                        var knot = editor.InsertReroute(link, Number(op, "x", 0), Number(op, "y", 0));
                        if ((string?)op["ref"] is { Length: > 0 } name) refs[name] = knot.Id;
                        break;
                    }
                    case "collapse":
                    {
                        editor.Selection.Clear(); editor.Selection.UnionWith(Ids(op, "nodes"));
                        editor.CollapseToFunction((string?)op["name"] ?? "Function");
                        if ((string?)op["ref"] is { Length: > 0 } name) refs[name] = editor.Selection.First();
                        break;
                    }
                    case "expand":
                        if (editor.ExpandFunctionCall(Id(op, "node")).Count == 0) problem = "not a function call node";
                        break;
                    case "align":
                    {
                        editor.Selection.Clear(); editor.Selection.UnionWith(Ids(op, "nodes"));
                        if (!Enum.TryParse<AlignMode>((string?)op["mode"] ?? "", true, out var mode)) { problem = "mode: left, right, top, bottom, centerhorizontal or centervertical"; break; }
                        editor.Align(mode);
                        break;
                    }
                    case "distribute":
                        editor.Selection.Clear(); editor.Selection.UnionWith(Ids(op, "nodes"));
                        editor.Distribute(((string?)op["axis"] ?? "horizontal").StartsWith('h'));
                        break;
                    case "toggle_collapsed": editor.ToggleCollapsed(Ids(op, "nodes")); break;
                    case "rename":
                        editor.Checkpoint("Rename");
                        editor.Document.Name = (string?)op["name"] ?? editor.Document.Name;
                        break;
                    default:
                        problem = $"unknown op '{kind}' (one of {string.Join(", ", Ops)})";
                        break;
                }
                if (problem is null) applied++; else errors.Add($"op {index} ({kind}): {problem}");
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or FormatException)
            {
                errors.Add($"op {index} ({kind}): {exception.Message}");
            }
        }
        var ids = new JsonObject();
        foreach (var (name, id) in refs) ids[name] = id;
        return new JsonObject { ["applied"] = applied, ["ids"] = ids, ["errors"] = errors };
    }
}
