using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solforge.Graphs;

/** A node instance: its type, canvas position, property values (input pin defaults share the pin's id) and fold state. */
public sealed class GraphNode
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public bool Collapsed { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);

    public string Get(string id, string fallback = "") => Properties.TryGetValue(id, out var value) ? value : fallback;
    public GraphNode Clone() => new() { Id = Id, Type = Type, X = X, Y = Y, Collapsed = Collapsed, Properties = new(Properties, StringComparer.Ordinal) };
}

/** A wire from an output pin to an input pin. */
public sealed record GraphLink(string FromNode, string FromPin, string ToNode, string ToPin);

/** A comment box; nodes inside it move with it. */
public sealed class GraphComment
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "Comment";
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; } = 400;
    public float Height { get; set; } = 200;
    public string Color { get; set; } = "#3A4A5A";
    public GraphComment Clone() => (GraphComment)MemberwiseClone();
}

/** A function signature pin. */
public sealed record FunctionPin(string Id, string Name, PinType Type);

/** A collapsed subgraph: its own nodes and links, with Function Inputs / Function Outputs nodes as its boundary. */
public sealed class GraphFunction
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Function";
    public bool Exec { get; set; }
    public List<FunctionPin> Inputs { get; set; } = [];
    public List<FunctionPin> Outputs { get; set; } = [];
    public List<GraphNode> Nodes { get; set; } = [];
    public List<GraphLink> Links { get; set; } = [];
}

/** A graph file (*.solgraph.json): material, weapon/item or mission. */
public sealed class GraphDocument
{
    public const string FormatName = "solforge-graph";
    public const string Extension = ".solgraph.json";
    public string Format { get; set; } = FormatName;
    public int Version { get; set; } = 1;
    public GraphKind Kind { get; set; }
    public string Name { get; set; } = "";
    public List<GraphNode> Nodes { get; set; } = [];
    public List<GraphLink> Links { get; set; } = [];
    public List<GraphComment> Comments { get; set; } = [];
    public List<GraphFunction> Functions { get; set; } = [];

    private static readonly JsonSerializerOptions json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, json);

    public static GraphDocument FromJson(string text)
    {
        var document = JsonSerializer.Deserialize<GraphDocument>(text, json) ?? throw new InvalidDataException("Empty graph file.");
        if (document.Format != FormatName) throw new InvalidDataException($"Not a {FormatName} file.");
        if (document.Version > 1) throw new InvalidDataException($"Graph version {document.Version} is newer than this Solforge.");
        document.Nodes ??= []; document.Links ??= []; document.Comments ??= []; document.Functions ??= [];
        foreach (var function in document.Functions) { function.Nodes ??= []; function.Links ??= []; function.Inputs ??= []; function.Outputs ??= []; }
        foreach (var node in document.Nodes.Concat(document.Functions.SelectMany(function => function.Nodes)))
            node.Properties = node.Properties is null ? new(StringComparer.Ordinal) : new(node.Properties, StringComparer.Ordinal);
        return document;
    }

    public static GraphDocument Load(string path) => FromJson(File.ReadAllText(path));

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, path, true);
    }

    public GraphDocument Clone() => FromJson(ToJson());

    public GraphNode? Node(string id) => Nodes.FirstOrDefault(node => node.Id == id);
    public GraphFunction? Function(string id) => Functions.FirstOrDefault(function => function.Id == id);

    /** A fresh id with this prefix not used by any node, comment or function. */
    public string NewId(string prefix)
    {
        var used = Nodes.Select(node => node.Id).Concat(Comments.Select(comment => comment.Id)).Concat(Functions.Select(function => function.Id))
            .Concat(Functions.SelectMany(function => function.Nodes.Select(node => node.Id))).ToHashSet(StringComparer.Ordinal);
        for (int index = used.Count + 1; ; index++)
            if (!used.Contains($"{prefix}{index}")) return $"{prefix}{index}";
    }

    /** A new graph with its output node, as File > New makes it. */
    public static GraphDocument Create(GraphKind kind, string name, string target = "")
    {
        var document = new GraphDocument { Kind = kind, Name = name };
        string? output = kind switch
        {
            GraphKind.Material => NodeLibrary.MaterialOutput,
            GraphKind.Weapon => NodeLibrary.WeaponOutput,
            GraphKind.Mission => "mission.start",
            _ => null
        };
        if (output is not null)
        {
            var node = new GraphNode { Id = "n1", Type = output, X = kind == GraphKind.Mission ? 0 : 600, Y = 0 };
            if (target.Length > 0 && kind != GraphKind.Mission) node.Properties["target"] = target;
            document.Nodes.Add(node);
        }
        return document;
    }

    /** The pins a node has here: its type's pins, or those its function, reroute or boundary gives it. */
    public IReadOnlyList<PinDef> PinsOf(GraphNode node, GraphFunction? scope = null)
    {
        if (NodeLibrary.Find(node.Type) is not { } type) return [];
        switch (node.Type)
        {
            case NodeLibrary.Reroute:
            {
                // A reroute takes the type of what feeds it, else of what it feeds.
                var links = scope?.Links ?? Links;
                var nodes = scope?.Nodes ?? Nodes;
                PinType resolved = ResolveReroute(node, nodes, links, scope, 0);
                return [new("in", "", resolved, false), new("out", "", resolved, true)];
            }
            case NodeLibrary.FunctionCall when Function(node.Get("function")) is { } function:
            {
                var pins = new List<PinDef>();
                if (function.Exec) { pins.Add(NodeLibrary.ExecIn); pins.Add(NodeLibrary.Then); }
                pins.AddRange(function.Inputs.Select(pin => new PinDef(pin.Id, pin.Name, pin.Type, false)));
                pins.AddRange(function.Outputs.Select(pin => new PinDef(pin.Id, pin.Name, pin.Type, true)));
                return pins;
            }
            case NodeLibrary.FunctionInput when scope is not null:
                return [.. scope.Exec ? new[] { NodeLibrary.Then } : Array.Empty<PinDef>(), .. scope.Inputs.Select(pin => new PinDef(pin.Id, pin.Name, pin.Type, true))];
            case NodeLibrary.FunctionOutput when scope is not null:
                return [.. scope.Exec ? new[] { NodeLibrary.ExecIn } : Array.Empty<PinDef>(), .. scope.Outputs.Select(pin => new PinDef(pin.Id, pin.Name, pin.Type, false))];
            default:
                return type.Pins;
        }
    }

    private PinType ResolveReroute(GraphNode node, List<GraphNode> nodes, List<GraphLink> links, GraphFunction? scope, int depth)
    {
        if (depth > 32) return PinType.Any;
        foreach (var link in links.Where(link => link.ToNode == node.Id))
            if (nodes.FirstOrDefault(other => other.Id == link.FromNode) is { } source)
            {
                var type = source.Type == NodeLibrary.Reroute ? ResolveReroute(source, nodes, links, scope, depth + 1)
                    : PinsOf(source, scope).FirstOrDefault(pin => pin.Output && pin.Id == link.FromPin)?.Type ?? PinType.Any;
                if (type != PinType.Any) return type;
            }
        foreach (var link in links.Where(link => link.FromNode == node.Id))
            if (nodes.FirstOrDefault(other => other.Id == link.ToNode) is { } target && target.Type != NodeLibrary.Reroute)
            {
                var type = PinsOf(target, scope).FirstOrDefault(pin => !pin.Output && pin.Id == link.ToPin)?.Type ?? PinType.Any;
                if (type is not (PinType.Any or PinType.Numeric)) return type;
            }
        return PinType.Any;
    }

    public PinDef? Pin(GraphNode node, string pin, bool output, GraphFunction? scope = null) =>
        PinsOf(node, scope).FirstOrDefault(def => def.Output == output && def.Id == pin);
}
