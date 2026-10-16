/**
 * File: Parsed flow and campaign graph of a mission script (groups, nodes, edges, diagnostics) with edits
 * that rewrite only source spans.
 */
namespace SunriseStudio.Missions;

/** Whether a graph group is a flow or a campaign. */
public enum MissionGraphKind { Flow, Campaign }
/** One step of a flow or campaign, with its table, entry field and dependencies. */
public sealed record MissionGraphNode(string Key, string Id, string GroupKey, string Kind,
    LuaTable? Table, LuaField Entry, IReadOnlyList<string>? Dependencies);
/** A link between two nodes: source, target and kind. */
public sealed record MissionGraphEdge(string Source, string Target, string Kind);
/** One flow or campaign table and the nodes it holds. */
public sealed record MissionGraphGroup(string Key, string Title, MissionGraphKind Kind, string Collection,
    LuaTable Table, IReadOnlyList<MissionGraphNode> Nodes)
{
    public LuaTable? Definition { get; init; }
    public bool IsFlowSteps => Kind == MissionGraphKind.Flow && Collection == "steps";
    public bool IsFlowFacts => Kind == MissionGraphKind.Flow && Collection == "facts";
    public bool IsCampaignSteps => Kind == MissionGraphKind.Campaign && Collection == "steps";
    public bool IsCampaignLegs => Kind == MissionGraphKind.Campaign && Collection == "legs";
    public bool IsCampaignEncounters => Kind == MissionGraphKind.Campaign && Collection == "encounters";
    /** True when both groups come from the same flow.new or campaign.new table. */
    public bool SameGraph(MissionGraphGroup other) => Definition?.Span == other.Definition?.Span;
}
/** A validation message, optionally tied to a node. */
public sealed record MissionGraphDiagnostic(string Message, string? Node = null);

/** Graph changes replace source spans; unhandled Lua is never regenerated. */
public sealed partial class MissionGraphDocument
{
    public string Source { get; }
    public IReadOnlyList<MissionGraphGroup> Groups { get; }
    public IReadOnlyList<MissionGraphNode> Nodes { get; }
    public IReadOnlyList<MissionGraphEdge> Edges { get; }
    public IReadOnlyList<MissionGraphDiagnostic> Diagnostics { get; }
    public string Newline => LuaText.Newline(Source);
    internal LuaScript Script { get; }

    /** Finds each X.new{...} call where X is a local bound to require("lib.flow") or require("lib.campaign"). */
    private MissionGraphDocument(string source)
    {
        Source = source; Script = LuaScript.Parse(source);
        var aliases = new Dictionary<string, MissionGraphKind>(StringComparer.Ordinal);
        var localTables = new Dictionary<string, LuaTable>(StringComparer.Ordinal);
        var calls = new List<LuaCall>();
        foreach (var node in Script.Descendants())
        {
            if (node is LuaCall call) calls.Add(call);
            if (node is not LuaLocalStatement { Names: [{ Attribute: null } local], Values: [var value, ..] }) continue;
            if (value is LuaTable table) localTables[local.Name] = table;
            if (value is LuaCall { Target: LuaName { Name: "require" }, Method: null,
                    Arguments: { Form: LuaArgumentsForm.List, Values: [LuaLiteral argument, ..] } }
                && argument.StringValue is "lib.flow" or "lib.campaign")
                aliases[local.Name] = argument.StringValue == "lib.flow" ? MissionGraphKind.Flow : MissionGraphKind.Campaign;
        }
        var groups = new List<MissionGraphGroup>();
        foreach (var call in calls)
        {
            if (call is not { Method: null, Target: LuaMember { Name: "new", Target: LuaName alias } } || !aliases.TryGetValue(alias.Name, out var kind)
                || call.Arguments.Values is not [LuaTable definition, ..]) continue;
            string key = definition.Field("key")?.StringValue ?? $"graph-{groups.Count + 1}";
            foreach (string collection in kind == MissionGraphKind.Flow ? new[] { "steps", "facts" } : ["steps", "legs", "encounters"])
            {
                var field = definition.Field(collection);
                var table = Resolve(field, localTables);
                if (table is null) continue;
                string groupKey = $"{kind}/{key}/{collection}";
                var nodes = new List<MissionGraphNode>();
                foreach (var entry in table.Fields.Where(value => value.Name is null))
                {
                    var body = Resolve(entry, localTables);
                    string id = body?.Field("id")?.StringValue ?? entry.Value.Read(source);
                    if (id.Length > 120) id = "Lua block " + (nodes.Count + 1);
                    string nodeKey = groupKey + "/" + id;
                    nodes.Add(new(nodeKey, id, groupKey, collection, body, entry, Dependencies(body?.Field("after"))));
                }
                groups.Add(new(groupKey, key + " / " + collection, kind, collection, table, nodes) { Definition = definition });
            }
        }
        Groups = groups;
        Nodes = groups.SelectMany(group => group.Nodes).ToArray();
        var edges = new List<MissionGraphEdge>();
        foreach (var group in groups)
        {
            if (group.IsCampaignSteps)
                for (int i = 1; i < group.Nodes.Count; i++) edges.Add(new(group.Nodes[i - 1].Key, group.Nodes[i].Key, "Next"));
            else if (group.IsFlowSteps)
                foreach (var node in group.Nodes)
                    foreach (string dependency in node.Dependencies ?? [])
                        edges.Add(new(group.Key + "/" + dependency, node.Key, "After"));
            if (group.IsCampaignEncounters)
                foreach (var node in group.Nodes)
                    if (node.Table?.Field("after")?.StringValue is { } after)
                    {
                        string stepGroup = group.Key[..group.Key.LastIndexOf('/')] + "/steps/";
                        edges.Add(new(stepGroup + after, node.Key, "Started"));
                    }
        }
        Edges = edges;
        Diagnostics = Validate();
    }

    /** A Lua syntax error throws, so an edit that breaks the script is refused. */
    public static MissionGraphDocument Parse(string source) => new(source);
    /** Node by key; throws when missing. */
    public MissionGraphNode Node(string key) => Nodes.First(value => value.Key == key);
    /** Group by key; throws when missing. */
    public MissionGraphGroup Group(string key) => Groups.First(value => value.Key == key);
    /** The group a node belongs to. */
    public MissionGraphGroup GroupOf(string nodeKey) => Group(Node(nodeKey).GroupKey);

    /** Nodes of one collection in the same flow or campaign as group. */
    public MissionGraphNode[] NodesIn(MissionGraphGroup group, string collection) =>
        Groups.Where(value => value.Collection == collection && group.SameGraph(value)).SelectMany(value => value.Nodes).ToArray();

    /** Source supplied for one field must remain inside one balanced table expression. */
    public MissionGraphDocument SetField(string key, string path, string expression)
    {
        var node = Node(key);
        var table = node.Table ?? throw new NotSupportedException("This node is supplied by Lua code.");
        string[] parts = path.Split('.');
        if (parts.Any(part => !LuaScript.IsIdentifier(part)) || parts[0] == "id")
            throw new InvalidDataException("Choose an editable Lua field.");
        LuaText.CheckExpression(expression);
        return ReplaceField(table, parts, expression);
    }

    /** Replaces or adds the field at path; missing parents are written as nested tables. */
    private MissionGraphDocument ReplaceField(LuaTable table, string[] path, string expression)
    {
        var field = table.Field(path[0]);
        if (path.Length > 1)
        {
            if (field?.Table is { } nested) return ReplaceField(nested, path[1..], expression);
            if (field is not null) throw new NotSupportedException("The parent field is supplied by Lua code.");
            for (int i = path.Length - 1; i >= 1; i--) expression = "{ " + path[i] + " = " + expression + " }";
        }
        if (field is not null) return Edit(field.Value, expression);
        return Parse(LuaText.InsertField(Source, table, path[0] + " = " + expression));
    }

    /** A node supplied by Lua code is replaced as one balanced expression. */
    public MissionGraphDocument SetEntry(string key, string expression)
    {
        LuaText.CheckExpression(expression);
        return Edit(Node(key).Entry.Value, expression);
    }

    /** Removes a named field from a node. */
    public MissionGraphDocument RemoveField(string key, string name)
    {
        var field = Field(key, name) ?? throw new InvalidDataException("Field does not exist.");
        if (name.Split('.')[0] == "id") throw new InvalidDataException("Node identity cannot be removed.");
        return Edit(field.Span, "");
    }

    /** A node field by dotted path, or null. */
    public LuaField? Field(string key, string path)
    {
        var table = Node(key).Table; LuaField? field = null;
        foreach (string name in path.Split('.')) { field = table?.Field(name); table = field?.Table; }
        return field;
    }

    /** Sets one field of the flow.new or campaign.new table that defines the group. */
    // TODO: no caller yet; the inspector has no editor for graph fields such as dialogue_sensor.
    public MissionGraphDocument SetDefinitionField(string groupKey, string field, string expression)
    {
        if (!LuaScript.IsIdentifier(field)) throw new InvalidDataException("Invalid graph field.");
        LuaText.CheckExpression(expression);
        return ReplaceField(Group(groupKey).Definition!, [field], expression);
    }

    /** Appending a list entry preserves every existing callback and expression in that list. */
    public MissionGraphDocument AppendValue(string key, string path, string expression)
    {
        LuaText.CheckExpression(expression);
        if (Field(key, path) is not { } field) return SetField(key, path, "{ " + expression + " }");
        var table = field.Table ?? throw new NotSupportedException("This list is supplied by Lua code.");
        return Parse(LuaText.InsertField(Source, table, expression, entry: true));
    }

    /** Replaces one item of a list field. */
    public MissionGraphDocument SetListValue(string key, string path, int index, string expression)
    {
        LuaText.CheckExpression(expression);
        var item = Field(key, path)?.Table?.Fields.ElementAtOrDefault(index)
            ?? throw new NotSupportedException("This list entry is supplied by Lua code.");
        return Edit(item.Value, expression);
    }

    /** Removes one item of a list field. */
    public MissionGraphDocument RemoveListValue(string key, string path, int index)
    {
        var item = Field(key, path)?.Table?.Fields.ElementAtOrDefault(index)
            ?? throw new NotSupportedException("This list entry is supplied by Lua code.");
        return Edit(item.Span, "");
    }

    /** Explicit dependencies belong to flow steps; campaign order is edited with MoveNode. */
    public MissionGraphDocument Connect(string from, string to, bool connect = true)
    {
        var source = Node(from); var target = Node(to);
        var group = Group(target.GroupKey);
        if (source.GroupKey != target.GroupKey || !group.IsFlowSteps)
            throw new InvalidOperationException("Only flow steps have dependency ports.");
        if (source.Key == target.Key) throw new InvalidDataException("A step cannot depend on itself.");
        var dependencies = target.Dependencies?.ToList() ?? throw new NotSupportedException("Replace dynamic dependencies in the source first.");
        dependencies.RemoveAll(value => value == source.Id);
        if (connect) dependencies.Add(source.Id);
        var next = SetField(to, "after", "{" + string.Join(", ", dependencies.Select(LuaScript.Quote)) + "}");
        next.RequireValid();
        return next;
    }

    /** Adding a node leaves surrounding callbacks and comments unchanged. */
    public MissionGraphDocument AddNode(string groupKey, string id)
    {
        var group = Group(groupKey);
        if (string.IsNullOrWhiteSpace(id) || id.Any(char.IsControl) || group.Nodes.Any(value => value.Id == id))
            throw new InvalidDataException("Use a unique nonempty node id.");
        if (group.Definition?.Field("key")?.StringValue is { } prefix)
        {
            if (group.IsCampaignEncounters) prefix += ".enc";
            string category = group.IsFlowFacts ? "fact" : "step";
            if (StateKeyTooLong(prefix, category, id))
                throw new InvalidDataException("The node id exceeds the runtime's 63-byte state-key limit.");
        }
        string field = group.IsFlowFacts ? ", observe = function(context, state, event) return false end"
            : group.IsFlowSteps ? ", run = function(context, state) end" : "";
        var next = Parse(LuaText.InsertField(Source, group.Table, "{ id = " + LuaScript.Quote(id) + field + " }", entry: true));
        next.RequireValid(); return next;
    }

    /** Deletes a node entry; throws while other steps still follow it. */
    public MissionGraphDocument RemoveNode(string key)
    {
        if (Edges.Any(edge => edge.Source == key && edge.Kind == "After"))
            throw new InvalidOperationException("Disconnect dependent steps first.");
        return Edit(Node(key).Entry.Span, "");
    }

    /** Reordering swaps complete entries, preserving their Lua bodies byte for byte. */
    public MissionGraphDocument MoveNode(string key, int direction)
    {
        var group = GroupOf(key);
        int index = group.Nodes.ToList().FindIndex(value => value.Key == key), other = index + Math.Sign(direction);
        if (other < 0 || other >= group.Nodes.Count) return this;
        var first = group.Nodes[Math.Min(index, other)].Entry;
        var last = group.Nodes[Math.Max(index, other)].Entry;
        string a = first.Value.Read(Source), b = last.Value.Read(Source);
        string changed = Source[..first.Value.Start] + b + Source[first.Value.End..last.Value.Start] + a + Source[last.Value.End..];
        return Parse(changed);
    }

    /** Throws listing the diagnostics when the document is not valid. */
    public void RequireValid()
    {
        if (Diagnostics.Count > 0) throw new InvalidDataException(string.Join("\n", Diagnostics.Select(value => value.Message)));
    }

    private MissionGraphDocument Edit(LuaSpan span, string replacement) => Parse(LuaText.Edit(Source, span, replacement));

    private LuaTable? Resolve(LuaField? field, IReadOnlyDictionary<string, LuaTable> locals) => field?.Table
        ?? (field is not null && locals.TryGetValue(field.Value.Read(Source), out var table) ? table : null);

    private IReadOnlyList<string>? Dependencies(LuaField? field)
    {
        if (field is null) return [];
        if (field.StringValue is { } value) return [value];
        if (field.Table is not { } table || table.Fields.Any(value => value.Name is not null || value.StringValue is null)) return null;
        return table.Fields.Select(value => value.StringValue!).ToArray();
    }

    /** Validation follows the installed flow dependency contract and campaign chain limit. */
    private IReadOnlyList<MissionGraphDiagnostic> Validate()
    {
        var result = new List<MissionGraphDiagnostic>();
        foreach (var group in Groups)
        {
            int maximum = group.IsCampaignSteps ? 62 : 64;
            if (group.Nodes.Count > maximum) result.Add(new($"{group.Title} exceeds {maximum} nodes."));
            foreach (var duplicate in group.Nodes.GroupBy(node => node.Id).Where(nodes => nodes.Count() > 1))
                result.Add(new("Duplicate node id: " + duplicate.Key));
            if (!group.IsFlowSteps) continue;
            var ids = group.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var node in group.Nodes)
                foreach (string dependency in node.Dependencies ?? [])
                    if (!ids.Contains(dependency) || dependency == node.Id)
                        result.Add(new("Invalid dependency: " + dependency, node.Key));
            var placed = new HashSet<string>(StringComparer.Ordinal);
            for (int pass = 0; pass < group.Nodes.Count; pass++)
                foreach (var node in group.Nodes)
                    if (node.Dependencies is null || node.Dependencies.All(placed.Contains)) placed.Add(node.Id);
            if (placed.Count != ids.Count) result.Add(new("Flow dependencies contain a cycle."));
        }
        ValidateFlowFields(result);
        return result;
    }
}
