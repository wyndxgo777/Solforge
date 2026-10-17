/**
 * File: Script graph: every statement of a mission script as a node, with edges, scopes and source-
 * preserving edits.
 */
using System.Text;

namespace SunriseStudio.Missions;

/** What kind of statement or entry a script node is. */
public enum MissionScriptNodeKind
{
    Module, Function, Flow, Value, Code, Callback, Setting,
    If, ElseIf, Else, While, For, Repeat, Until, Scope, Call, Assignment, Return, Break, ApiCall
}

/** A node input, named, with the span of its expression. */
public sealed record MissionScriptInput(string Name, LuaSpan Span);
/** A body of statements owned by a node. */
public sealed record MissionScriptScope(string Key, string Title, string Owner, LuaBlock Body);

/**
 * One statement, return-table field or function-body statement shown as a node.
 * @param Span Source that an edit of this node replaces, with the comment lines above it.
 * @param First Index in the script's tokens of the node's first token.
 * @param End Token index after the node's code.
 * @param Function The function whose body opens as a sub-graph.
 */
public sealed record MissionScriptNode(string Key, MissionScriptNodeKind Kind, string Title, LuaSpan Span,
    int First, int End, LuaFunction? Function)
{
    public IReadOnlyList<string> Defines { get; init; } = [];
    public bool HasValue { get; init; } = true;
    /** Flow group key prefix, such as "Flow/deadly_trial", when this statement defines a flow or campaign. */
    public string? FlowGroup { get; init; }
    /** Span removed with the node: the code plus its separator and the rest of its line. */
    public LuaSpan RemoveSpan { get; init; }
    public string Scope { get; init; } = MissionScriptGraph.Root;
    public IReadOnlyList<MissionScriptInput> Inputs { get; init; } = [];
    public LuaControlBlock? Control { get; init; }
    public bool Structural { get; init; }
    public string? FallbackReason { get; init; }
    public string? CallbackOwner { get; init; }
    public string? CallbackPath { get; init; }
    /** The typed API call this statement is; null for any other statement. */
    public MissionApiCall? ApiCall { get; init; }
    /** Typed API calls in this node's own code, in source order; calls inside a nested function are not its own. */
    public IReadOnlyList<MissionApiCall> ApiCalls { get; init; } = [];
    /** The mission-library call (lib.mission_lib transition, cutscene, boss phase, ending) this statement is; null otherwise. */
    public Solforge.Graphs.MissionLibMatch? LibCall { get; init; }
    /** The events a returned callback serves; empty for a start callback and for every other node. */
    public IReadOnlyList<MissionApiEvent> ApiEvents { get; init; } = [];
    internal object? SourceIdentity { get; init; }
}

/** A link between two script nodes with a label. */
public sealed record MissionScriptEdge(string Source, string Target, string Label);

/** The whole script as a graph: statements are nodes and a name's definition links to each use. */
public sealed partial class MissionScriptGraph
{
    public const string Root = "script";
    private readonly LuaScript script;
    private readonly object sourceIdentity = new();
    private readonly LuaTable? returnTable;
    public string Source { get; }
    public IReadOnlyList<MissionScriptNode> Nodes { get; }
    public IReadOnlyList<MissionScriptEdge> Edges { get; }
    public IReadOnlyList<MissionScriptNode> Callbacks { get; }
    /** The script's calls typed against the API schema; null without a schema or when typing failed. */
    public MissionApiAnalysis? Api { get; }
    /** Why Api is null when a schema was given; empty otherwise. */
    public string ApiError { get; } = "";

    /** One node per top-level statement, except a returned table, which gives one node per field. */
    private MissionScriptGraph(string source, IReadOnlyList<MissionGraphGroup> flows, MissionApiContext? api = null)
    {
        Source = source;
        script = LuaScript.Parse(source);
        if (api is not null)
        {
            try { Api = MissionApiAnalysis.Analyze(script, api); }
            catch (InvalidDataException error) { ApiError = error.Message; }
        }
        var nodes = new List<MissionScriptNode>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var statement in script.Chunk.Statements)
        {
            if (statement is LuaReturnStatement { Values: [LuaTable table] })
            {
                returnTable = table;
                AddFields(table, nodes, keys);
                continue;
            }
            nodes.Add(Describe(Classify(statement, flows, keys, ordinals), statement));
        }
        Nodes = nodes;
        Edges = Link(nodes);
        Callbacks = ReadCallbacks(flows);
    }

    /** @param api The API schema and SDK entries; without them every call stays a Lua block. */
    public static MissionScriptGraph Parse(string source, IReadOnlyList<MissionGraphGroup>? flows = null, MissionApiContext? api = null) =>
        new(source, flows ?? [], api);

    /** Node by key; throws when missing. */
    public MissionScriptNode Node(string key) => FindNode(key)
        ?? throw new InvalidDataException("Script node does not exist.");

    /** The code a node shows and edits. */
    public string Code(MissionScriptNode node) => node.Span.Read(Source);

    /** Replaces a node's code; the result must still parse as a script. */
    public string Replace(MissionScriptNode node, string code)
    {
        node = CurrentNode(node);
        return code == Code(node) ? Source : Checked(Edit(node.Span, Lines(code)));
    }

    private string Lines(string code) => LuaText.Lines(Source, code);

    /**
     * Removes the node from the source; branch and structural nodes follow special rules. Returns the new
     * source.
     */
    public string Remove(MissionScriptNode node)
    {
        node = CurrentNode(node);
        if (node.Kind is MissionScriptNodeKind.ElseIf or MissionScriptNodeKind.Else) return RemoveBranch(node);
        return node.Structural ? throw new InvalidOperationException("Edit this branch through its owning control node.")
            : Checked(Edit(node.RemoveSpan, ""));
    }

    /** Swaps a node with its neighbour among siblings, keeping both bodies byte for byte. */
    public string Move(IReadOnlyList<MissionScriptNode> siblings, string key, int direction)
    {
        var selected = siblings.FirstOrDefault(node => node.Key == key);
        if (selected is null || selected.Structural) return Source;
        _ = CurrentNode(selected);
        siblings = siblings.Where(node => node.Scope == selected.Scope && !node.Structural).ToArray();
        foreach (var sibling in siblings) _ = CurrentNode(sibling);
        int index = siblings.ToList().FindIndex(value => value.Key == key), other = index + Math.Sign(direction);
        if (index < 0 || other < 0 || other >= siblings.Count) return Source;
        // A returned-table field and a statement cannot trade places.
        if (IsField(siblings[index]) != IsField(siblings[other])) return Source;
        var first = siblings[Math.Min(index, other)].Span; var last = siblings[Math.Max(index, other)].Span;
        return Checked(Source[..first.Start] + last.Read(Source) + Source[first.End..last.Start] + first.Read(Source) + Source[last.End..]);
    }

    /** Adds a top-level statement before the final return, or at the end when there is none. */
    public string AddStatement(string code)
    {
        var last = script.Chunk.Statements.LastOrDefault();
        if (last is { Keyword: "return" }) return Checked(Edit(new(last.Span.Start, 0), Lines(code) + Newline + Newline));
        return Checked(Source.TrimEnd() + Newline + Newline + Lines(code) + Newline);
    }

    /** Adds a field such as a callback to the returned table. */
    public string AddField(string code)
    {
        var table = returnTable ?? throw new InvalidOperationException("The script does not return a callback table.");
        string separator = table.Fields.Count == 0 || Source[table.Fields[^1].Span.End - 1] is ',' or ';' ? "" : ",";
        int close = table.Span.End - 1;
        // A closing brace on its own line keeps it; the new field takes the line above.
        int line = LuaText.LineStart(Source, close);
        // Every line of the field takes the field's indent.
        string field = IndentUnit + Lines(code).Replace(Newline, Newline + IndentUnit, StringComparison.Ordinal);
        if (separator.Length == 0 && Source[line..close].Trim().Length == 0)
            return Checked(Edit(new(line, 0), field + "," + Newline));
        return Checked(Edit(new(close, 0), separator + Newline + field + "," + Newline));
    }

    /** New statements stay before a function's final return. */
    public string AddToBody(MissionScriptNode owner, string code)
    {
        owner = CurrentNode(owner);
        var function = owner.Function ?? throw new InvalidOperationException("This node has no function body.");
        return InsertStatement(function.Body, owner, code);
    }

    /** A top-level statement as a function, flow, module, value or code node. */
    private MissionScriptNode Classify(LuaStatement statement, IReadOnlyList<MissionGraphGroup> flows,
        HashSet<string> keys, Dictionary<string, int> ordinals)
    {
        int first = TokenAt(statement.Code.Start), end = TokenAt(statement.Code.End);
        var flow = flows.FirstOrDefault(group => group.Definition is { } definition
            && definition.Span.Start >= statement.Span.Start && definition.Span.End <= statement.Span.End);
        string? flowGroup = flow is null ? null : flow.Key[..flow.Key.LastIndexOf('/')];
        var removal = LineSpan(statement.Span);
        if (statement is LuaFunctionStatement declared)
            return new(Unique(keys, "function/" + declared.Name), MissionScriptNodeKind.Function, Signature(declared.Name, declared.Function),
                statement.Span, first, end, declared.Function)
                { Defines = declared.Local || LuaScript.IsIdentifier(declared.Name) ? [declared.Name] : [], RemoveSpan = removal };
        var names = new List<string>();
        IReadOnlyList<LuaExpression> values = [];
        int value = -1;
        if (statement is LuaLocalStatement local)
        {
            names.AddRange(local.Names.Select(name => name.Name));
            if (local.EqualsSign is { } sign) { value = TokenAt(sign.End); values = local.Values; }
        }
        else if (statement is LuaAssignmentStatement assignment)
        {
            // A plain top-level assignment such as `graph = flow.new{...}` defines its name.
            int assign = TokenAt(assignment.EqualsSign.Start);
            value = assign + 1; values = assignment.Values;
            for (int i = first, depth = 0; i < assign; depth += Bracket(i), i++) if (IsReference(i, first, depth)) names.Add(Text(i));
        }
        if (flowGroup is not null)
            return new(Unique(keys, "flow/" + flowGroup[(flowGroup.IndexOf('/') + 1)..]), MissionScriptNodeKind.Flow,
                flowGroup.Replace('/', ' '), statement.Span, first, end, null) { Defines = names, FlowGroup = flowGroup, RemoveSpan = removal };
        if (names.Count > 0 && value > 0 && Text(value) == "require")
            return new(Unique(keys, "module/" + names[0]), MissionScriptNodeKind.Module,
                names[0] + " = " + StatementTitle(value, end), statement.Span, first, end, null) { Defines = names, RemoveSpan = removal };
        if (names.Count == 1 && values is [LuaFunction function])
            return new(Unique(keys, "function/" + names[0]), MissionScriptNodeKind.Function, Signature(names[0], function), statement.Span,
                first, end, function) { Defines = names, RemoveSpan = removal };
        if (names.Count > 0)
            return new(Unique(keys, "value/" + names[0]), MissionScriptNodeKind.Value, (statement.Keyword == "local" ? "local " : "")
                + string.Join(", ", names), statement.Span, first, end, null) { Defines = names, HasValue = value > 0, RemoveSpan = removal };
        int ordinal = ordinals[statement.Keyword] = ordinals.GetValueOrDefault(statement.Keyword) + 1;
        return new(Unique(keys, "code/" + statement.Keyword + ordinal), MissionScriptNodeKind.Code, StatementTitle(first, end),
            statement.Span, first, end, null) { RemoveSpan = removal };
    }

    /** Callbacks and settings are the fields of the returned table. */
    private void AddFields(LuaTable table, List<MissionScriptNode> nodes, HashSet<string> keys)
    {
        int previous = table.Span.Start + 1, index = 0;
        foreach (var field in table.Fields)
        {
            index++;
            // A [key] = value field's node starts at its key and never opens as a function.
            int valueFirst = TokenAt(field.Key is null ? field.Value.Start : field.Span.Start), end = TokenAt(field.Value.End);
            int start = script.LeadingComments(previous, field.Span.Start);
            var span = LuaSpan.FromBounds(start, field.Value.End);
            var removal = LineSpan(LuaSpan.FromBounds(start, field.Span.End));
            previous = field.Span.End;
            string name = field.Name ?? "[" + index + "]";
            bool callback = name.StartsWith("on_", StringComparison.Ordinal);
            var function = field.Key is null ? field.Expression as LuaFunction : null;
            var events = Api?.Api.Schema.EventForCallback(name) is { } served ? new[] { served } : [];
            // Links scan only the value; the field name is not a use of a top-level name.
            nodes.Add(new(Unique(keys, "field/" + name), callback ? MissionScriptNodeKind.Callback : MissionScriptNodeKind.Setting,
                name, span, valueFirst, end, function)
            {
                RemoveSpan = removal, SourceIdentity = sourceIdentity, ApiEvents = events,
                ApiCalls = function is null ? OwnedCalls([field.Value]) : []
            });
        }
    }

    /** A definition links to every later node that names it; a name with no top-level definition is not linked. */
    private List<MissionScriptEdge> Link(IReadOnlyList<MissionScriptNode> nodes)
    {
        var definers = new Dictionary<string, MissionScriptNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
            foreach (string name in node.Defines)
                if (node.HasValue || !definers.ContainsKey(name)) definers[name] = node;
        var edges = new List<MissionScriptEdge>();
        foreach (var node in nodes)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = node.First, depth = 0; i < node.End; depth += Bracket(i), i++)
            {
                if (!IsReference(i, node.First, depth) || node.Defines.Contains(Text(i)) || !seen.Add(Text(i))) continue;
                if (definers.TryGetValue(Text(i), out var source) && source != node) edges.Add(new(source.Key, node.Key, Text(i)));
            }
        }
        return edges;
    }

    /**
     * A bare name use: not a member after '.' or ':', not a table key, not a keyword.
     * @param depth Bracket depth at token i, counted from first.
     */
    private bool IsReference(int i, int first, int depth)
    {
        var tokens = script.Tokens;
        if (i >= tokens.Count || tokens[i].Kind == LuaTokenKind.String || !LuaScript.IsName(tokens[i].Text))
            return false;
        if (i > 0 && tokens[i - 1].Text is "." or ":") return false;
        return !(i > first && tokens[i - 1].Text is "{" or "," or ";" && Text(i + 1) == "=" && depth > 0);
    }

    /** +1 opens a bracket, -1 closes one; strings count 0. */
    private int Bracket(int i) => IsString(i) ? 0 : Text(i) is "(" or "{" or "[" ? 1 : Text(i) is ")" or "}" or "]" ? -1 : 0;

    /** A one-line summary: lib.flow calls get a name, other code shows its first tokens. */
    private string StatementTitle(int first, int end)
    {
        string keyword = Text(first);
        if (keyword is "if" or "for" or "while") return keyword + " " + Snippet(first + 1, end, keyword == "if" ? "then" : "do");
        if (keyword is "repeat" or "do" or "function") return keyword + " block";
        string compact = string.Concat(Enumerable.Range(first, Math.Min(end - first, 400)).Select(Text));
        foreach (var (pattern, title) in KnownCalls)
            if (compact.Contains(pattern, StringComparison.Ordinal)) return title;
        if (keyword == "return") return end - first > 1 ? "return " + Snippet(first + 1, end, "") : "return";
        if (keyword == "local") return "local " + Snippet(first + 1, end, "=");
        return Snippet(first, end, "");
    }

    /** Tokens from first up to stop, spaced like source and cut at 48 characters. */
    private string Snippet(int first, int end, string stop)
    {
        var text = new StringBuilder();
        for (int i = first; i < end && Text(i) != stop && text.Length < 48; i++)
        {
            string token = Text(i);
            bool word = LuaScript.IsIdentifier(token) || IsString(i) || char.IsDigit(token[0]);
            bool previousWord = i > first && (LuaScript.IsIdentifier(Text(i - 1)) || IsString(i - 1) || char.IsDigit(Text(i - 1)[0]));
            if (text.Length > 0 && (word && previousWord || Text(i - 1) == "," || BinaryText(token) || BinaryText(Text(i - 1)))) text.Append(' ');
            text.Append(token);
        }
        return text.Length >= 48 ? text.ToString(0, 48) + "..." : text.ToString();
    }

    // lib.flow methods; Sunrise API calls take their titles from the schema.
    private static readonly (string Pattern, string Title)[] KnownCalls =
    [
        (":advance(", "Advance flow"), (":handle(", "Send event to flow")
    ];

    /** Typed calls inside these spans and outside any function nested in them. */
    private IReadOnlyList<MissionApiCall> OwnedCalls(IReadOnlyList<LuaSpan> spans)
    {
        if (Api is null || spans.Count == 0) return [];
        var calls = Api.Calls.Where(call => call.Outcome == MissionApiOutcome.Typed && spans.Any(span => Inside(call.Call.Span, span))).ToArray();
        if (calls.Length == 0) return [];
        functionBodies ??= script.Descendants().OfType<LuaFunction>().Select(function => function.Body.Span).ToArray();
        var nested = functionBodies.Where(body => spans.Any(span => Inside(body, span))).ToArray();
        return calls.Where(call => !nested.Any(span => Inside(call.Call.Span, span))).OrderBy(call => call.Call.Span.Start).ToArray();

        static bool Inside(LuaSpan inner, LuaSpan outer) => inner.Start >= outer.Start && inner.End <= outer.End;
    }

    private LuaSpan[]? functionBodies;

    private static string Signature(string name, LuaFunction function) => name + "(" + string.Join(", ", function.Parameters) + ")";

    /** Extends a span to the end of its line when only whitespace follows, so removal leaves no blank line. */
    private LuaSpan LineSpan(LuaSpan span)
    {
        int end = span.End;
        while (end < Source.Length && Source[end] is ' ' or '\t' or ',' or ';') end++;
        if (end < Source.Length && Source[end] == '\r') end++;
        if (end < Source.Length && Source[end] == '\n') end++;
        else end = span.End;
        int start = span.Start;
        while (start > 0 && Source[start - 1] is ' ' or '\t') start--;
        return new(start, end - start);
    }

    private int TokenAt(int position) => script.TokenAt(position);

    private string Indent(int position) => LuaText.Indent(Source, position);
    private string IndentUnit => LuaText.IndentUnit(Source);

    private static string Unique(HashSet<string> keys, string key)
    {
        string result = Root + "/" + key;
        for (int n = 2; !keys.Add(result); n++) result = Root + "/" + key + "#" + n;
        return result;
    }

    private static bool BinaryText(string token) => token is "=" or "==" or "~=" or "<=" or ">=" or "<" or ">" or ".." or "and" or "or";

    private static bool IsField(MissionScriptNode node) => node.Kind is MissionScriptNodeKind.Callback or MissionScriptNodeKind.Setting;
    private string Edit(LuaSpan span, string text) => LuaText.Edit(Source, span, text);
    private string Newline => LuaText.Newline(Source);
    private string Text(int i) => i >= 0 && i < script.Tokens.Count ? script.Tokens[i].Text : "";
    private bool IsString(int i) => script.Tokens[i].Kind == LuaTokenKind.String;

    /** An edit must leave a script this reader can still split into statements. */
    private static string Checked(string source)
    {
        var parsed = new MissionScriptGraph(source, []);
        _ = parsed.AllNodes().Count();
        return source;
    }
}
