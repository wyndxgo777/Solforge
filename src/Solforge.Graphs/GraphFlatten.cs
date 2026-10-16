namespace Solforge.Graphs;

/**
 * A graph with every function call inlined, as compilers read it. Reroutes stay as nodes; Sources and Targets
 * look through them.
 */
public sealed class FlatGraph
{
    public required GraphDocument Document { get; init; }
    public required List<GraphNode> Nodes { get; init; }
    public required List<GraphLink> Links { get; init; }
    private Dictionary<string, GraphNode>? byId;

    public GraphNode? Node(string id) => (byId ??= Nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First()))
        .GetValueOrDefault(id);

    /** The output pins feeding an input, through reroutes. */
    public IEnumerable<(GraphNode Node, string Pin)> Sources(string nodeId, string pin, int depth = 0)
    {
        foreach (var link in Links.Where(link => link.ToNode == nodeId && link.ToPin == pin))
        {
            if (Node(link.FromNode) is not { } source) continue;
            if (source.Type == NodeLibrary.Reroute && depth < 64)
            {
                foreach (var inner in Sources(source.Id, "in", depth + 1)) yield return inner;
            }
            else yield return (source, link.FromPin);
        }
    }

    /** The input pins an output feeds, through reroutes. */
    public IEnumerable<(GraphNode Node, string Pin)> Targets(string nodeId, string pin, int depth = 0)
    {
        foreach (var link in Links.Where(link => link.FromNode == nodeId && link.FromPin == pin))
        {
            if (Node(link.ToNode) is not { } target) continue;
            if (target.Type == NodeLibrary.Reroute && depth < 64)
            {
                foreach (var inner in Targets(target.Id, "out", depth + 1)) yield return inner;
            }
            else yield return (target, link.ToPin);
        }
    }

    public IEnumerable<GraphNode> OfType(string type) => Nodes.Where(node => node.Type == type);
}

public static class GraphFlatten
{
    private const int MaxDepth = 16;

    /** Inlines every function call; ids of inlined nodes are "<call id>/<inner id>". */
    public static FlatGraph Flatten(GraphDocument document)
    {
        var nodes = document.Nodes.Select(node => node.Clone()).ToList();
        var links = document.Links.ToList();
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            var call = nodes.FirstOrDefault(node => node.Type == NodeLibrary.FunctionCall);
            if (call is null) break;
            if (document.Function(call.Get("function")) is not { } function) { nodes.Remove(call); links.RemoveAll(link => link.FromNode == call.Id || link.ToNode == call.Id); continue; }
            Inline(nodes, links, call, function);
        }
        nodes.RemoveAll(node => node.Type == NodeLibrary.FunctionCall);
        return new() { Document = document, Nodes = nodes, Links = links };
    }

    /** Replaces one call node with a copy of its function's body, rewiring its links. Returns the new nodes. */
    public static List<GraphNode> Inline(List<GraphNode> nodes, List<GraphLink> links, GraphNode call, GraphFunction function)
    {
        string prefix = call.Id + "/";
        var input = function.Nodes.FirstOrDefault(node => node.Type == NodeLibrary.FunctionInput);
        var output = function.Nodes.FirstOrDefault(node => node.Type == NodeLibrary.FunctionOutput);
        var external = links.Where(link => link.FromNode == call.Id || link.ToNode == call.Id).ToList();
        IEnumerable<(string Node, string Pin)> SourcesFor(string pin) =>
            external.Where(link => link.ToNode == call.Id && link.ToPin == pin).Select(link => (link.FromNode, link.FromPin));
        IEnumerable<(string Node, string Pin)> TargetsFor(string pin) =>
            external.Where(link => link.FromNode == call.Id && link.FromPin == pin).Select(link => (link.ToNode, link.ToPin));
        static string CallIn(string boundaryPin) => boundaryPin == "then" ? "exec" : boundaryPin;
        static string CallOut(string boundaryPin) => boundaryPin == "exec" ? "then" : boundaryPin;

        var added = new List<GraphNode>();
        float minX = function.Nodes.Count == 0 ? 0 : function.Nodes.Min(node => node.X), minY = function.Nodes.Count == 0 ? 0 : function.Nodes.Min(node => node.Y);
        foreach (var inner in function.Nodes)
        {
            if (inner.Type is NodeLibrary.FunctionInput or NodeLibrary.FunctionOutput) continue;
            var copy = inner.Clone();
            copy.Id = prefix + inner.Id;
            copy.X = call.X + inner.X - minX; copy.Y = call.Y + inner.Y - minY;
            added.Add(copy);
        }
        links.RemoveAll(link => link.FromNode == call.Id || link.ToNode == call.Id);
        foreach (var link in function.Links)
        {
            bool fromBoundary = input is not null && link.FromNode == input.Id, toBoundary = output is not null && link.ToNode == output.Id;
            if (fromBoundary && toBoundary)
            {
                foreach (var source in SourcesFor(CallIn(link.FromPin)))
                    foreach (var target in TargetsFor(CallOut(link.ToPin)))
                        links.Add(new(source.Node, source.Pin, target.Node, target.Pin));
            }
            else if (fromBoundary)
            {
                var sources = SourcesFor(CallIn(link.FromPin)).ToList();
                foreach (var source in sources) links.Add(new(source.Node, source.Pin, prefix + link.ToNode, link.ToPin));
                // An unlinked call input keeps the value typed on the call node.
                if (sources.Count == 0 && call.Properties.TryGetValue(link.FromPin, out var value)
                    && added.FirstOrDefault(node => node.Id == prefix + link.ToNode) is { } target)
                    target.Properties[link.ToPin] = value;
            }
            else if (toBoundary)
            {
                foreach (var target in TargetsFor(CallOut(link.ToPin))) links.Add(new(prefix + link.FromNode, link.FromPin, target.Node, target.Pin));
            }
            else links.Add(new(prefix + link.FromNode, link.FromPin, prefix + link.ToNode, link.ToPin));
        }
        nodes.Remove(call);
        nodes.AddRange(added);
        return added;
    }
}
