/**
 * File: Interactive graph editing model: selection, undo/redo, add/connect/move/align/duplicate. The ImGui canvas is
 * in Editor/StudioForm.NodeGraph.cs.
 */
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Graphs;

/** How Align lines up the selected nodes. */
public enum AlignMode { Left, Right, Top, Bottom, CenterHorizontal, CenterVertical }

/** Node box sizes in canvas units at zoom 1; the canvas draws with these and align/distribute measure with them. */
public static class GraphLayout
{
    public const float Width = 220, CollapsedWidth = 170, Header = 30, Row = 22, Padding = 8, RerouteSize = 14;

    /** Box size of a node in canvas units (reroutes and collapsed nodes are smaller than normal ones). */
    public static (float Width, float Height) Size(GraphDocument document, GraphNode node, GraphFunction? scope = null)
    {
        if (node.Type == NodeLibrary.Reroute) return (RerouteSize, RerouteSize);
        if (node.Collapsed) return (CollapsedWidth, Header);
        var pins = document.PinsOf(node, scope);
        int rows = Math.Max(pins.Count(pin => !pin.Output), pins.Count(pin => pin.Output));
        int properties = NodeLibrary.Find(node.Type)?.Properties.Count(property => property.Kind != PropertyKind.Code) ?? 0;
        if (node.Type == NodeLibrary.FunctionCall) properties = 0;
        return (Width, Header + (rows + properties) * Row + Padding);
    }
}

/**
 * Every edit of a graph: add, link (with conversion nodes), remove, move, properties, reroutes, comments, clipboard,
 * align and distribute, fold, functions. Each edit checkpoints first, so Undo and Redo cover all of them.
 * Scope is the function being edited, or null for the main graph.
 */
public sealed class GraphEditor
{
    private const int MaxUndo = 200;
    private readonly List<(string Json, string? Scope, string Label)> undo = [], redo = [];
    private string? scopeId;

    /** Wraps a document for editing. */
    public GraphEditor(GraphDocument document) { Document = document; }

    public GraphDocument Document { get; private set; }
    /** Ids of the selected nodes. */
    public HashSet<string> Selection { get; } = new(StringComparer.Ordinal);
    /** Bumped on every change, so views and validation know to refresh. */
    public int Revision { get; private set; }
    public bool Dirty { get; set; }
    public GraphFunction? Scope => scopeId is null ? null : Document.Function(scopeId);
    public List<GraphNode> Nodes => Scope?.Nodes ?? Document.Nodes;
    public List<GraphLink> Links => Scope?.Links ?? Document.Links;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string UndoLabel => undo.Count > 0 ? undo[^1].Label : "";
    public string RedoLabel => redo.Count > 0 ? redo[^1].Label : "";

    /** Finds a node in the current scope by id. */
    public GraphNode? Node(string id) => Nodes.FirstOrDefault(node => node.Id == id);
    /** Pins of a node in the current scope. */
    public IReadOnlyList<PinDef> PinsOf(GraphNode node) => Document.PinsOf(node, Scope);

    /** Opens a function's body for editing (null returns to the main graph). */
    public void Enter(string? functionId)
    {
        scopeId = functionId is not null && Document.Function(functionId) is not null ? functionId : null;
        Selection.Clear();
        Revision++;
    }

    /** Records the state before an edit. Drag edits call it once when the drag starts. */
    public void Checkpoint(string label)
    {
        undo.Add((Document.ToJson(), scopeId, label));
        if (undo.Count > MaxUndo) undo.RemoveAt(0);
        redo.Clear();
        Dirty = true;
        Revision++;
    }

    /** Undoes the last change; false when nothing to undo. */
    public bool Undo() => Swap(undo, redo);
    /** Redoes the last undone change; false when nothing to redo. */
    public bool Redo() => Swap(redo, undo);

    /** Moves the newest snapshot from one stack onto the other and restores it. */
    private bool Swap(List<(string Json, string? Scope, string Label)> from, List<(string Json, string? Scope, string Label)> to)
    {
        if (from.Count == 0) return false;
        var (json, scope, label) = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add((Document.ToJson(), scopeId, label));
        Document = GraphDocument.FromJson(json);
        scopeId = scope;
        Selection.RemoveWhere(id => Node(id) is null && Document.Comments.All(comment => comment.Id != id));
        Dirty = true;
        Revision++;
        return true;
    }

    /** Replaces the document (a reload from disk); history is kept so the reload itself can be undone. */
    public void Replace(GraphDocument document, string label = "Reload")
    {
        Checkpoint(label);
        Document = document;
        scopeId = null;
        Selection.Clear();
    }

    /** Adds a node of a registered type at canvas x,y with an undo step; throws for unknown types. */
    public GraphNode AddNode(string type, float x, float y, IReadOnlyDictionary<string, string>? properties = null)
    {
        var definition = NodeLibrary.Find(type) ?? throw new ArgumentException($"Unknown node type {type}.");
        Checkpoint("Add " + definition.Title);
        return AddRaw(type, x, y, properties);
    }

    /** Creates the node and fills default property values without an undo checkpoint (used by compound edits). */
    private GraphNode AddRaw(string type, float x, float y, IReadOnlyDictionary<string, string>? properties = null)
    {
        var node = new GraphNode { Id = Document.NewId("n"), Type = type, X = x, Y = y };
        foreach (var property in NodeLibrary.Find(type)?.Properties ?? Array.Empty<PropertyDef>())
            if (property.Default.Length > 0) node.Properties[property.Id] = property.Default;
        if (properties is not null) foreach (var (key, value) in properties) node.Properties[key] = value;
        Nodes.Add(node);
        Revision++;
        return node;
    }

    /** Why a link from -> to is refused, or null when it is allowed (perhaps through a conversion). */
    public string? CanConnect(string fromNode, string fromPin, string toNode, string toPin)
    {
        if (fromNode == toNode) return "A node cannot link to itself.";
        if (Node(fromNode) is not { } source || Node(toNode) is not { } target) return "No such node.";
        if (Document.Pin(source, fromPin, true, Scope) is not { } output) return $"{fromNode} has no output '{fromPin}'.";
        if (Document.Pin(target, toPin, false, Scope) is not { } input) return $"{toNode} has no input '{toPin}'.";
        if (!PinTypes.Connectable(output.Type, input.Type))
            return $"{PinTypes.Label(output.Type)} cannot connect to {PinTypes.Label(input.Type)}.";
        if (output.Type != PinType.Exec && input.Type != PinType.Exec && Reaches(toNode, fromNode))
            return "That link would make a loop.";
        return null;
    }

    /**
     * Links an output to an input. A single input (and an exec output) drops its old link; mismatched data types get
     * a conversion node in between. Returns the error, or null.
     */
    public string? Connect(string fromNode, string fromPin, string toNode, string toPin)
    {
        if (CanConnect(fromNode, fromPin, toNode, toPin) is { } problem) return problem;
        Checkpoint("Connect");
        ConnectRaw(fromNode, fromPin, toNode, toPin);
        return null;
    }

    /** Adds a link without an undo checkpoint (callers checkpoint and validate first). */
    private void ConnectRaw(string fromNode, string fromPin, string toNode, string toPin)
    {
        var source = Node(fromNode)!; var target = Node(toNode)!;
        var output = Document.Pin(source, fromPin, true, Scope)!;
        var input = Document.Pin(target, toPin, false, Scope)!;
        if (!input.Multi && input.Type != PinType.Exec) Links.RemoveAll(link => link.ToNode == toNode && link.ToPin == toPin);
        if (output.Type == PinType.Exec) Links.RemoveAll(link => link.FromNode == fromNode && link.FromPin == fromPin);
        Links.RemoveAll(link => link == new GraphLink(fromNode, fromPin, toNode, toPin));
        if (!PinTypes.Compatible(output.Type, input.Type) && PinTypes.Conversion(output.Type, input.Type) is { } conversion)
        {
            var (width, _) = GraphLayout.Size(Document, source, Scope);
            var middle = AddRaw(conversion, (source.X + width + target.X) / 2 - 40, (source.Y + target.Y) / 2);
            Links.Add(new(fromNode, fromPin, middle.Id, "in"));
            Links.Add(new(middle.Id, "out", toNode, toPin));
        }
        else Links.Add(new(fromNode, fromPin, toNode, toPin));
        Revision++;
    }

    /** Whether data flows from start to goal already (so goal -> start would loop). */
    private bool Reaches(string start, string goal)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([start]);
        while (stack.TryPop(out var id))
        {
            if (id == goal) return true;
            if (!seen.Add(id)) continue;
            foreach (var link in Links.Where(link => link.FromNode == id))
                if (Node(id) is { } node && Document.Pin(node, link.FromPin, true, Scope)?.Type != PinType.Exec) stack.Push(link.ToNode);
        }
        return false;
    }

    /** Removes one link (undoable). */
    public void Disconnect(GraphLink link)
    {
        if (!Links.Contains(link)) return;
        Checkpoint("Disconnect");
        Links.Remove(link);
    }

    /** Alt-click on a pin: cuts every link on it. */
    public void BreakPin(string nodeId, string pin, bool output)
    {
        if (!Links.Any(link => output ? link.FromNode == nodeId && link.FromPin == pin : link.ToNode == nodeId && link.ToPin == pin)) return;
        Checkpoint("Break links");
        Links.RemoveAll(link => output ? link.FromNode == nodeId && link.FromPin == pin : link.ToNode == nodeId && link.ToPin == pin);
    }

    /** Removes nodes (with their links) and comments. */
    public void Remove(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0) return;
        Checkpoint("Delete");
        Nodes.RemoveAll(node => set.Contains(node.Id));
        Links.RemoveAll(link => set.Contains(link.FromNode) || set.Contains(link.ToNode));
        if (Scope is null) Document.Comments.RemoveAll(comment => set.Contains(comment.Id));
        Selection.ExceptWith(set);
    }

    /** Moves without a checkpoint; a drag checkpoints once when it starts. Comments carry the nodes inside them. */
    public void Move(IEnumerable<string> ids, float dx, float dy)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var comment in Document.Comments.Where(comment => set.Contains(comment.Id)).ToList())
        {
            foreach (var node in Nodes.Where(node => Inside(comment, node))) set.Add(node.Id);
            comment.X += dx; comment.Y += dy;
        }
        foreach (var node in Nodes.Where(node => set.Contains(node.Id))) { node.X += dx; node.Y += dy; }
        Dirty = true;
        Revision++;
    }

    /** Whether a node lies fully inside a comment box. */
    public bool Inside(GraphComment comment, GraphNode node)
    {
        var (width, height) = GraphLayout.Size(Document, node, Scope);
        return node.X >= comment.X && node.Y >= comment.Y && node.X + width <= comment.X + comment.Width && node.Y + height <= comment.Y + comment.Height;
    }

    /** Sets a node property value (undoable; no-op when unchanged). */
    public void SetProperty(string nodeId, string key, string value)
    {
        if (Node(nodeId) is not { } node || node.Get(key, "\0") == value) return;
        Checkpoint("Set " + key);
        node.Properties[key] = value;
    }

    /** Folds the given nodes (or unfolds them all when every one is already folded). */
    public void ToggleCollapsed(IEnumerable<string> ids)
    {
        var nodes = Nodes.Where(node => ids.Contains(node.Id)).ToList();
        if (nodes.Count == 0) return;
        Checkpoint("Collapse");
        bool fold = nodes.Any(node => !node.Collapsed);
        foreach (var node in nodes) node.Collapsed = fold;
    }

    /** Splits a link with a reroute node at (x, y). */
    public GraphNode InsertReroute(GraphLink link, float x, float y)
    {
        Checkpoint("Add reroute");
        Links.Remove(link);
        var knot = AddRaw(NodeLibrary.Reroute, x - GraphLayout.RerouteSize / 2, y - GraphLayout.RerouteSize / 2);
        Links.Add(new(link.FromNode, link.FromPin, knot.Id, "in"));
        Links.Add(new(knot.Id, "out", link.ToNode, link.ToPin));
        return knot;
    }

    /** A comment around the selection, or a default box at (x, y). */
    public GraphComment AddComment(float x, float y, string text = "Comment")
    {
        Checkpoint("Add comment");
        var comment = new GraphComment { Id = Document.NewId("c"), Text = text, X = x, Y = y };
        var selected = Nodes.Where(node => Selection.Contains(node.Id)).ToList();
        if (selected.Count > 0)
        {
            const float margin = 30;
            comment.X = selected.Min(node => node.X) - margin;
            comment.Y = selected.Min(node => node.Y) - margin - 24;
            comment.Width = selected.Max(node => node.X + GraphLayout.Size(Document, node, Scope).Width) - comment.X + margin;
            comment.Height = selected.Max(node => node.Y + GraphLayout.Size(Document, node, Scope).Height) - comment.Y + margin;
        }
        Document.Comments.Add(comment);
        return comment;
    }

    /** Applies a change to a comment box (undoable). */
    public void EditComment(string id, Action<GraphComment> change)
    {
        if (Document.Comments.FirstOrDefault(comment => comment.Id == id) is not { } comment) return;
        Checkpoint("Edit comment");
        change(comment);
    }

    // Clipboard.

    public const string ClipFormat = "solforge-graph-clip";

    /** The selected nodes, the links among them and the selected comments, as clipboard text. */
    public string Copy(IEnumerable<string>? ids = null)
    {
        var set = (ids ?? Selection).ToHashSet(StringComparer.Ordinal);
        var nodes = Nodes.Where(node => set.Contains(node.Id)).ToList();
        var clip = new GraphDocument
        {
            Format = ClipFormat, Kind = Document.Kind, Nodes = nodes.Select(node => node.Clone()).ToList(),
            Links = Links.Where(link => set.Contains(link.FromNode) && set.Contains(link.ToNode)).ToList(),
            Comments = Scope is null ? Document.Comments.Where(comment => set.Contains(comment.Id)).Select(comment => comment.Clone()).ToList() : [],
            Functions = Document.Functions.Where(function => nodes.Any(node => node.Type == NodeLibrary.FunctionCall && node.Get("function") == function.Id)).ToList()
        };
        return clip.ToJson();
    }

    /** Pastes clipboard text with its top-left at (x, y); returns the new ids (now selected), or empty for foreign text. */
    public IReadOnlyList<string> Paste(string text, float x, float y)
    {
        GraphDocument clip;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root || (string?)root["format"] != ClipFormat) return [];
            clip = GraphDocument.FromJson(text.Replace($"\"{ClipFormat}\"", $"\"{GraphDocument.FormatName}\"", StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException) { return []; }
        if (clip.Nodes.Count == 0 && clip.Comments.Count == 0) return [];
        Checkpoint("Paste");
        float minX = clip.Nodes.Select(node => node.X).Concat(clip.Comments.Select(comment => comment.X)).Min();
        float minY = clip.Nodes.Select(node => node.Y).Concat(clip.Comments.Select(comment => comment.Y)).Min();
        foreach (var function in clip.Functions.Where(function => Document.Function(function.Id) is null)) Document.Functions.Add(function);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var node in clip.Nodes)
        {
            if (NodeLibrary.Find(node.Type) is null) continue;
            var copy = node.Clone();
            copy.Id = Document.NewId("n"); copy.X = node.X - minX + x; copy.Y = node.Y - minY + y;
            map[node.Id] = copy.Id;
            Nodes.Add(copy); added.Add(copy.Id);
        }
        foreach (var link in clip.Links)
            if (map.TryGetValue(link.FromNode, out var from) && map.TryGetValue(link.ToNode, out var to)) Links.Add(new(from, link.FromPin, to, link.ToPin));
        if (Scope is null)
            foreach (var comment in clip.Comments)
            {
                var copy = comment.Clone();
                copy.Id = Document.NewId("c"); copy.X = comment.X - minX + x; copy.Y = comment.Y - minY + y;
                Document.Comments.Add(copy); added.Add(copy.Id);
            }
        Selection.Clear(); Selection.UnionWith(added);
        return added;
    }

    /** Duplicates the selected nodes and the links between them; returns the new ids. */
    public IReadOnlyList<string> Duplicate()
    {
        var nodes = Nodes.Where(node => Selection.Contains(node.Id)).ToList();
        if (nodes.Count == 0) return [];
        return Paste(Copy(), nodes.Min(node => node.X) + 40, nodes.Min(node => node.Y) + 40);
    }

    // Align and distribute.

    /** Aligns the selected nodes (two or more) per the mode. */
    public void Align(AlignMode mode)
    {
        var nodes = Nodes.Where(node => Selection.Contains(node.Id)).ToList();
        if (nodes.Count < 2) return;
        Checkpoint("Align");
        var sizes = nodes.ToDictionary(node => node.Id, node => GraphLayout.Size(Document, node, Scope));
        float left = nodes.Min(node => node.X), top = nodes.Min(node => node.Y);
        float right = nodes.Max(node => node.X + sizes[node.Id].Width), bottom = nodes.Max(node => node.Y + sizes[node.Id].Height);
        foreach (var node in nodes)
        {
            var (width, height) = sizes[node.Id];
            switch (mode)
            {
                case AlignMode.Left: node.X = left; break;
                case AlignMode.Right: node.X = right - width; break;
                case AlignMode.Top: node.Y = top; break;
                case AlignMode.Bottom: node.Y = bottom - height; break;
                case AlignMode.CenterHorizontal: node.X = (left + right) / 2 - width / 2; break;
                case AlignMode.CenterVertical: node.Y = (top + bottom) / 2 - height / 2; break;
            }
        }
    }

    /** Spaces the selection evenly between its outermost nodes (equal gaps). */
    public void Distribute(bool horizontal)
    {
        var nodes = Nodes.Where(node => Selection.Contains(node.Id)).OrderBy(node => horizontal ? node.X : node.Y).ToList();
        if (nodes.Count < 3) return;
        Checkpoint("Distribute");
        float Extent(GraphNode node) => horizontal ? GraphLayout.Size(Document, node, Scope).Width : GraphLayout.Size(Document, node, Scope).Height;
        float start = horizontal ? nodes[0].X : nodes[0].Y;
        float end = horizontal ? nodes[^1].X + Extent(nodes[^1]) : nodes[^1].Y + Extent(nodes[^1]);
        float gap = (end - start - nodes.Sum(Extent)) / (nodes.Count - 1);
        float cursor = start;
        foreach (var node in nodes)
        {
            if (horizontal) node.X = cursor; else node.Y = cursor;
            cursor += Extent(node) + gap;
        }
    }

    // Functions (collapsed subgraphs).

    /** Moves the selected nodes into a new function and leaves a call node in their place. */
    public GraphFunction CollapseToFunction(string name)
    {
        if (Scope is not null) throw new InvalidOperationException("Collapse from the main graph.");
        var set = Nodes.Where(node => Selection.Contains(node.Id) && node.Type is not (NodeLibrary.FunctionInput or NodeLibrary.FunctionOutput))
            .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        if (set.Count == 0) throw new InvalidOperationException("Select the nodes to collapse.");
        var inbound = Links.Where(link => !set.Contains(link.FromNode) && set.Contains(link.ToNode)).ToList();
        var outbound = Links.Where(link => set.Contains(link.FromNode) && !set.Contains(link.ToNode)).ToList();
        PinType TypeOf(GraphLink link, bool output) =>
            output ? Document.Pin(Node(link.FromNode)!, link.FromPin, true)?.Type ?? PinType.Any
                : Document.Pin(Node(link.ToNode)!, link.ToPin, false)?.Type ?? PinType.Any;
        var execIn = inbound.Where(link => TypeOf(link, false) == PinType.Exec).ToList();
        var execOut = outbound.Where(link => TypeOf(link, true) == PinType.Exec).ToList();
        if (execIn.Select(link => (link.ToNode, link.ToPin)).Distinct().Count() > 1
            || execOut.Select(link => (link.FromNode, link.FromPin)).Distinct().Count() > 1)
            throw new InvalidOperationException("A function has one exec entry and one exec exit; select a single chain.");

        Checkpoint("Collapse to function");
        var nodes = Nodes.Where(node => set.Contains(node.Id)).ToList();
        var function = new GraphFunction { Id = Document.NewId("f"), Name = name, Exec = execIn.Count > 0 || execOut.Count > 0 };
        float minX = nodes.Min(node => node.X), minY = nodes.Min(node => node.Y), maxX = nodes.Max(node => node.X + GraphLayout.Width);
        var input = new GraphNode { Id = "in", Type = NodeLibrary.FunctionInput, X = minX - 280, Y = minY };
        var output = new GraphNode { Id = "out", Type = NodeLibrary.FunctionOutput, X = maxX + 60, Y = minY };
        function.Nodes.Add(input); function.Nodes.Add(output);
        function.Nodes.AddRange(nodes);
        function.Links.AddRange(Links.Where(link => set.Contains(link.FromNode) && set.Contains(link.ToNode)));
        var call = new GraphNode { Id = Document.NewId("n"), Type = NodeLibrary.FunctionCall, X = minX, Y = minY };
        call.Properties["function"] = function.Id;

        var newLinks = new List<GraphLink>();
        // Data in: one function input per distinct outside source.
        foreach (var group in inbound.Where(link => TypeOf(link, false) != PinType.Exec).GroupBy(link => (link.FromNode, link.FromPin)))
        {
            string pin = "i" + (function.Inputs.Count + 1);
            function.Inputs.Add(new(pin, Node(group.Key.FromNode)?.Type is { } type ? $"{NodeLibrary.Find(type)?.Title} {group.Key.FromPin}".Trim() : pin,
                TypeOf(group.First(), true)));
            newLinks.Add(new(group.Key.FromNode, group.Key.FromPin, call.Id, pin));
            foreach (var link in group) function.Links.Add(new(input.Id, pin, link.ToNode, link.ToPin));
        }
        // Data out: one function output per distinct inside source.
        foreach (var group in outbound.Where(link => TypeOf(link, true) != PinType.Exec).GroupBy(link => (link.FromNode, link.FromPin)))
        {
            string pin = "o" + (function.Outputs.Count + 1);
            function.Outputs.Add(new(pin, $"{NodeLibrary.Find(Node(group.Key.FromNode)!.Type)?.Title} {group.Key.FromPin}".Trim(), TypeOf(group.First(), true)));
            function.Links.Add(new(group.Key.FromNode, group.Key.FromPin, output.Id, pin));
            foreach (var link in group) newLinks.Add(new(call.Id, pin, link.ToNode, link.ToPin));
        }
        foreach (var link in execIn)
        {
            newLinks.Add(new(link.FromNode, link.FromPin, call.Id, "exec"));
            if (!function.Links.Any(inner => inner.FromNode == input.Id && inner.FromPin == "then")) function.Links.Add(new(input.Id, "then", link.ToNode, link.ToPin));
        }
        foreach (var link in execOut)
        {
            newLinks.Add(new(call.Id, "then", link.ToNode, link.ToPin));
            if (!function.Links.Any(inner => inner.ToNode == output.Id && inner.ToPin == "exec")) function.Links.Add(new(link.FromNode, link.FromPin, output.Id, "exec"));
        }
        Links.RemoveAll(link => set.Contains(link.FromNode) || set.Contains(link.ToNode));
        Nodes.RemoveAll(node => set.Contains(node.Id));
        Document.Functions.Add(function);
        Nodes.Add(call);
        Links.AddRange(newLinks);
        Selection.Clear(); Selection.Add(call.Id);
        return function;
    }

    /** Replaces a call node with the function's body (the function stays for other calls). */
    public IReadOnlyList<string> ExpandFunctionCall(string callId)
    {
        if (Scope is not null || Node(callId) is not { Type: NodeLibrary.FunctionCall } call
            || Document.Function(call.Get("function")) is not { } function) return [];
        Checkpoint("Expand function");
        var added = GraphFlatten.Inline(Document.Nodes, Document.Links, call, function);
        // Give inlined nodes plain ids.
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in added) { string fresh = Document.NewId("n"); map[node.Id] = fresh; node.Id = fresh; }
        for (int i = 0; i < Document.Links.Count; i++)
        {
            var link = Document.Links[i];
            Document.Links[i] = link with { FromNode = map.GetValueOrDefault(link.FromNode, link.FromNode), ToNode = map.GetValueOrDefault(link.ToNode, link.ToNode) };
        }
        Selection.Clear(); Selection.UnionWith(added.Select(node => node.Id));
        return [.. Selection];
    }

    /** The add menu for this graph, including Call entries for its functions. */
    public IReadOnlyList<NodeSearchResult> Search(string query, PinType? dragged = null, bool draggedIsOutput = true)
    {
        var calls = Document.Functions.Where(function => function.Id != scopeId).Select(function => new NodeType(
            NodeLibrary.FunctionCall + ":" + function.Id, "Call " + function.Name, "Functions", Document.Kind,
            [.. function.Exec ? new[] { NodeLibrary.ExecIn, NodeLibrary.Then } : Array.Empty<PinDef>(),
             .. function.Inputs.Select(pin => new PinDef(pin.Id, pin.Name, pin.Type, false)),
             .. function.Outputs.Select(pin => new PinDef(pin.Id, pin.Name, pin.Type, true))], [], "Runs " + function.Name));
        return NodeLibrary.Search(Document.Kind, query, dragged, draggedIsOutput, calls.ToList());
    }

    /**
     * Adds a search result at (x, y) and, when the menu came from a dragged pin, links it to that pin.
     * Returns the new node.
     */
    public GraphNode AddFromSearch(NodeSearchResult result, float x, float y, (string Node, string Pin, bool Output)? from = null)
    {
        string type = result.Type.Id, function = "";
        if (type.StartsWith(NodeLibrary.FunctionCall + ":", StringComparison.Ordinal))
        {
            function = type[(NodeLibrary.FunctionCall.Length + 1)..];
            type = NodeLibrary.FunctionCall;
        }
        Checkpoint("Add " + result.Type.Title);
        var node = AddRaw(type, x, y, function.Length > 0 ? new Dictionary<string, string> { ["function"] = function } : null);
        if (from is { } pin && result.PinId is { } target)
        {
            bool ok = pin.Output ? CanConnect(pin.Node, pin.Pin, node.Id, target) is null : CanConnect(node.Id, target, pin.Node, pin.Pin) is null;
            if (ok)
            {
                if (pin.Output) ConnectRaw(pin.Node, pin.Pin, node.Id, target);
                else ConnectRaw(node.Id, target, pin.Node, pin.Pin);
            }
        }
        Selection.Clear(); Selection.Add(node.Id);
        return node;
    }
}
