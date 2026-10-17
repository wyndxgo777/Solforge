/** File: Partial MissionScriptGraph: opening, scoping and editing control-flow bodies of script nodes. */
namespace SunriseStudio.Missions;

/** Partial: control-flow operations. */
public sealed partial class MissionScriptGraph
{
    private readonly Dictionary<string, (IReadOnlyList<MissionScriptNode> Nodes, IReadOnlyList<MissionScriptEdge> Edges)> bodies = [];
    private readonly Dictionary<string, MissionScriptScope> scopes = [];

    /** True when the node has a function or control body to open. */
    public bool CanOpen(MissionScriptNode node) => node.Function is not null || node.Control is not null;

    /** Nested nodes are found by source-derived keys, never by retained offsets from another parse. */
    public MissionScriptNode? FindNode(string key) => AllNodes().FirstOrDefault(node => node.Key == key);

    /** Top-level nodes first, then the nodes of every body they open; nesting past 64 is refused. */
    private IEnumerable<MissionScriptNode> AllNodes()
    {
        var top = Nodes.Concat(Callbacks).ToArray();
        foreach (var node in top) yield return node;
        foreach (var owner in top.Where(CanOpen))
            foreach (var node in Nested(owner, 0)) yield return node;

        IEnumerable<MissionScriptNode> Nested(MissionScriptNode owner, int depth)
        {
            if (depth > 64) throw new InvalidDataException("Lua function nesting exceeds the graph limit.");
            var body = Body(owner).Nodes;
            foreach (var node in body) yield return node;
            foreach (var child in body.Where(node => node.Function is not null && node.Key != owner.Key))
                foreach (var node in Nested(child, depth + 1)) yield return node;
        }
    }

    /** Every mission-library call node (transitions, cutscenes, boss phases, endings) at any depth, in walk order. */
    public IReadOnlyList<MissionScriptNode> LibraryCalls() => AllNodes().Where(node => node.LibCall is not null).ToArray();

    /** The scopes (bodies) a node owns, in source order. */
    public IReadOnlyList<MissionScriptScope> Scopes(MissionScriptNode owner)
    {
        _ = Body(owner);
        return scopes.Values.Where(scope => scope.Owner == owner.Key).OrderBy(scope => scope.Body.AfterHeader).ToArray();
    }

    /** Editing one input keeps the node's bodies, comments and unrelated expressions byte for byte. */
    public string SetInput(MissionScriptNode node, string name, string expression)
    {
        var current = CurrentNode(node);
        var input = current.Inputs.FirstOrDefault(value => value.Name == name)
            ?? throw new InvalidOperationException("This node has no such input.");
        if (expression == input.Span.Read(Source)) return Source;
        ValidateInput(current, name, expression);
        string replacement = Lines(expression);
        if (current.Kind == MissionScriptNodeKind.Return && input.Span.Length == 0)
            replacement = " " + replacement;
        if (name == "Arguments" && Text(TokenAt(input.Span.Start) - 1) != "(")
            replacement = "(" + replacement + ")";
        return Checked(Edit(input.Span, replacement));
    }

    /** Inserts a statement into a scope and returns the new source; break needs an enclosing loop. */
    public string AddToScope(string key, string code)
    {
        var scope = FindScope(key) ?? throw new InvalidOperationException("The script scope changed; open it again.");
        if (LuaScript.Parse(code).Chunk.Statements.Any(statement => statement is LuaBreakStatement) && !CanBreak(key))
            throw new InvalidOperationException("Break requires an enclosing loop.");
        return InsertStatement(scope.Body, Node(scope.Owner), code);
    }

    /** Typed names visible where a new statement of this scope goes; the script's top level for null. */
    public IReadOnlyDictionary<string, MissionApiType> ScopeNames(string? key)
    {
        if (Api is null) return new Dictionary<string, MissionApiType>();
        if (key is null) return Api.ScopeAt(script.Chunk);
        return FindScope(key) is { } scope ? Api.ScopeAt(scope.Body) : new Dictionary<string, MissionApiType>();
    }

    /** Loads the bodies that create this scope key, then returns the scope or null. */
    private MissionScriptScope? FindScope(string key)
    {
        foreach (var node in Nodes.Concat(Callbacks).Where(CanOpen)) _ = Body(node);
        if (!scopes.ContainsKey(key) && key.LastIndexOf("/scope/", StringComparison.Ordinal) is var split && split >= 0
            && FindNode(key[..split]) is { } nested) _ = Body(nested);
        return scopes.GetValueOrDefault(key);
    }

    /** True when a loop encloses the scope before any function body does. */
    public bool CanBreak(string scope)
    {
        var seen = new HashSet<string>();
        while (scopes.TryGetValue(scope, out var current) && seen.Add(scope))
        {
            var owner = Node(current.Owner);
            if (owner.Function is not null) return false;
            if (owner.Kind is MissionScriptNodeKind.For or MissionScriptNodeKind.While or MissionScriptNodeKind.Repeat) return true;
            scope = owner.Scope;
        }
        return false;
    }

    /** New branches are inserted before the final else, so existing branch priority stays unchanged. */
    public string AddBranch(MissionScriptNode owner, string? condition)
    {
        owner = CurrentNode(owner);
        if (owner.Control is not { Kind: "if" } block) throw new InvalidOperationException("Choose an if node.");
        if (condition is not null) LuaScript.ParseExpression(condition);
        var otherwise = block.Bodies.FirstOrDefault(body => body.Label == "else");
        if (condition is null && otherwise is not null) throw new InvalidOperationException("This if already has an else branch.");
        int at = otherwise?.Span.Start ?? block.Bodies[^1].Closing;
        string indent = Indent(script.Tokens[owner.First].Span.Start);
        string header = condition is null ? "else" : "elseif " + condition + " then";
        return Checked(Edit(new(at, 0), header + Newline + indent + IndentUnit + Newline + indent));
    }

    /** Removes an elseif or else branch with its body. */
    public string RemoveBranch(MissionScriptNode branch)
    {
        branch = CurrentNode(branch);
        if (branch.Kind is not (MissionScriptNodeKind.ElseIf or MissionScriptNodeKind.Else))
            throw new InvalidOperationException("Choose an else or else-if branch.");
        int start = branch.Span.Start, end = branch.Span.End;
        while (start > 0 && Source[start - 1] is ' ' or '\t') start--;
        int closingLine = LuaText.LineStart(Source, end);
        if (Source[closingLine..end].All(value => value is ' ' or '\t')) end = closingLine;
        string separator = start > 0 && Source[start - 1] != '\n' ? " " : "";
        return Checked(Edit(new(start, end - start), separator));
    }

    /** Inserts code at the end of a body, before its final return, at the body's indent. */
    private string InsertStatement(LuaBlock body, MissionScriptNode owner, string code)
    {
        var statements = body.Statements;
        var last = statements.LastOrDefault();
        string indent = statements.Count == 0 ? Indent(script.Tokens[owner.First].Span.Start) + IndentUnit
            : Indent(statements[0].Code.Start);
        int at = last is { Keyword: "return" } ? last.Span.Start : body.Closing;
        int line = LuaText.LineStart(Source, at);
        bool ownLine = Source[line..at].All(value => value is ' ' or '\t');
        string insertion = indent + Lines(code).Replace(Newline, Newline + indent, StringComparison.Ordinal) + Newline;
        if (ownLine) at = line;
        else insertion = Newline + insertion + Indent(script.Tokens[owner.First].Span.Start);
        return Checked(Edit(new(at, 0), insertion));
    }

    private MissionScriptNode CurrentNode(MissionScriptNode node)
    {
        if (!ReferenceEquals(node.SourceIdentity, sourceIdentity) || FindNode(node.Key) is not { } current
            || current.Span != node.Span || current.Kind != node.Kind)
            throw new InvalidOperationException("The script node changed; select it again.");
        return current;
    }

    private MissionScriptNode Describe(MissionScriptNode node, LuaStatement statement)
    {
        var described = DescribeStatement(node, statement);
        if (described.Kind is MissionScriptNodeKind.Function or MissionScriptNodeKind.Flow) return described;
        // A control node owns only its header; its bodies are nodes of their own.
        var spans = described.Control is not null ? described.Inputs.Select(input => input.Span).ToArray() : [statement.Code];
        return described with { ApiCalls = OwnedCalls(spans) };
    }

    /** A statement that is one typed API call becomes a typed node; a call the schema cannot type says why. */
    private MissionScriptNode? DescribeApiCall(MissionScriptNode node, LuaStatement statement)
    {
        if (Api is null || statement is not LuaCallStatement { Expression: LuaCall call } || Api.Find(call) is not { } typed) return null;
        if (typed.Outcome == MissionApiOutcome.Typed)
            return node with { Kind = MissionScriptNodeKind.ApiCall, Title = MissionApiText.Friendly(typed.Member!.Name), ApiCall = typed };
        if (typed.Outcome == MissionApiOutcome.NotApi) return null;
        string why = typed.Outcome switch
        {
            MissionApiOutcome.UnknownReceiver => "unknown receiver", MissionApiOutcome.UnknownMember => "unknown member",
            _ => "dynamic call target"
        };
        return node with { Kind = MissionScriptNodeKind.Code, FallbackReason = $"Custom Lua: {why} ({typed.Reason})" };
    }

    /** Sets a statement node's kind and inputs; a form the editor cannot type stays Code with a reason. */
    private MissionScriptNode DescribeStatement(MissionScriptNode node, LuaStatement statement)
    {
        node = node with { SourceIdentity = sourceIdentity };
        if (node.Kind is MissionScriptNodeKind.Function or MissionScriptNodeKind.Flow or MissionScriptNodeKind.Module
            or MissionScriptNodeKind.Callback or MissionScriptNodeKind.Setting) return node;
        // A mission-library call is matched before the API: its names (transition) can also be SDK member names.
        if (statement is LuaCallStatement { Expression: LuaCall libCall } && IsPath(libCall.Target) && libCall.Method is null
            && Solforge.Graphs.MissionLibrary.Match(libCall.Callee.Read(Source), libCall.Arguments.Inner.Read(Source)) is { } library)
            return node with { Kind = MissionScriptNodeKind.Call, Title = library.Call.Title, LibCall = library,
                Inputs = [new("Call", libCall.Callee), new("Arguments", libCall.Arguments.Inner)] };
        if (DescribeApiCall(node, statement) is { } api) return api;
        int first = node.First, end = node.End;
        if (Text(end - 1) == ";") end--;
        switch (statement)
        {
            case LuaControlStatement { Control: var block }:
                var kind = block.Kind switch
                {
                    "if" => MissionScriptNodeKind.If, "while" => MissionScriptNodeKind.While,
                    "for" => MissionScriptNodeKind.For, "repeat" => MissionScriptNodeKind.Repeat, _ => MissionScriptNodeKind.Scope
                };
                return node with { Kind = kind, Control = block,
                    Inputs = block.Input is { } input && block.Kind != "repeat"
                        ? [new(block.Kind == "for" ? "Loop" : "Condition", input)] : [] };
            case LuaReturnStatement:
                return node with { Kind = MissionScriptNodeKind.Return,
                    Inputs = [new("Values", first + 1 == end ? new(script.Tokens[first].Span.End, 0) : TokenSpan(first + 1, end))] };
            case LuaBreakStatement: return node with { Kind = MissionScriptNodeKind.Break };
            case LuaGotoStatement or LuaLabelStatement:
                return node with { Kind = MissionScriptNodeKind.Code, FallbackReason = "Custom Lua: label jump" };
        }
        var sign = statement switch { LuaLocalStatement local => local.EqualsSign, LuaAssignmentStatement assignment => assignment.EqualsSign, _ => null };
        if (sign is { } at)
        {
            int equals = TokenAt(at.Start), name = statement is LuaLocalStatement ? first + 1 : first;
            bool plain = Enumerable.Range(name, equals - name).All(i => Text(i) == "," || LuaScript.IsName(Text(i)));
            if (plain) return node with { Kind = MissionScriptNodeKind.Assignment,
                Inputs = [new("Names", TokenSpan(name, equals)), new("Values", TokenSpan(equals + 1, end))] };
            return node with { Kind = MissionScriptNodeKind.Code,
                FallbackReason = "Custom Lua: indexed or attributed assignment" };
        }
        if (statement is LuaLocalStatement attributed)
        {
            if (attributed.Names.Any(name => name.Attribute is not null))
                return node with { Kind = MissionScriptNodeKind.Code, FallbackReason = "Custom Lua: attributed local" };
            return node with { Kind = MissionScriptNodeKind.Assignment, Inputs = [new("Names", TokenSpan(first + 1, end))] };
        }
        // Only a call on a name or dotted path gets Call and Arguments inputs.
        if (statement is LuaCallStatement { Expression: LuaCall call } && IsPath(call.Target))
            return node with { Kind = MissionScriptNodeKind.Call,
                Inputs = [new("Call", call.Callee), new("Arguments", call.Arguments.Inner)] };
        return node with { Kind = MissionScriptNodeKind.Code,
            FallbackReason = "Custom Lua: dynamic call target" };
    }

    private static bool IsPath(LuaExpression expression) => expression switch
    {
        LuaName => true, LuaMember member => IsPath(member.Target), _ => false
    };

    private LuaSpan TokenSpan(int first, int end) => first == end
        ? new(first < script.Tokens.Count ? script.Tokens[first].Span.Start : Source.Length, 0)
        : new(script.Tokens[first].Span.Start, script.Tokens[end - 1].Span.End - script.Tokens[first].Span.Start);

    /** Flow fields retain their authored owner and path when callbacks open as control-flow graphs. */
    private IReadOnlyList<MissionScriptNode> ReadCallbacks(IReadOnlyList<MissionGraphGroup> groups)
    {
        var result = new List<MissionScriptNode>();
        foreach (var group in groups)
            foreach (var node in group.Nodes)
                if (node.Table is { } table) Visit(table, node.Key, node.Id, "", 0);
        return result;

        void Visit(LuaTable table, string owner, string title, string path, int depth)
        {
            if (depth > 32) throw new InvalidDataException("Callback field nesting exceeds the graph limit.");
            foreach (var field in table.Fields)
            {
                if (field.Name is null) continue;
                string name = path.Length == 0 ? field.Name : path + "." + field.Name;
                if (field.Expression is LuaFunction function)
                    result.Add(new(Root + "/callback/" + owner + "/" + name, MissionScriptNodeKind.Callback,
                        title + "." + name, field.Value, TokenAt(field.Value.Start), TokenAt(field.Value.End), function)
                        { Structural = true, CallbackOwner = owner, CallbackPath = name, RemoveSpan = field.Value, SourceIdentity = sourceIdentity });
                else if (field.Table is { } child) Visit(child, owner, title, name, depth + 1);
            }
        }
    }

    /** Refuses a value that does not fit its input: names, a call target, a loop header or expressions. */
    private static void ValidateInput(MissionScriptNode node, string name, string value)
    {
        if (name == "Names")
        {
            bool wantedName = true;
            foreach (var token in Tokens(value))
            {
                bool valid = wantedName ? token.Kind != LuaTokenKind.String && LuaScript.IsName(token.Text) : token.Text == ",";
                if (!valid) throw new InvalidDataException("Use comma-separated Lua names.");
                wantedName = !wantedName;
            }
            if (wantedName) throw new InvalidDataException("A Lua name is required.");
        }
        else if (name == "Call")
        {
            bool wantedName = true, method = false;
            foreach (var token in Tokens(value))
            {
                bool valid = wantedName ? token.Kind != LuaTokenKind.String && LuaScript.IsName(token.Text)
                    : !method && token.Text is "." or ":";
                if (!valid) throw new InvalidDataException("Use a named function or method target.");
                if (token.Text == ":") method = true;
                wantedName = !wantedName;
            }
            if (wantedName) throw new InvalidDataException("A call target is required.");
        }
        else if (name == "Loop")
        {
            if (LuaScript.Parse("for " + value + " do end").Chunk.Statements is not [LuaControlStatement { Control.Kind: "for" }])
                throw new InvalidDataException("Use a counted or iterator loop header.");
        }
        else if (LuaScript.ParseExpressions(value, name == "Arguments" || node.Kind == MissionScriptNodeKind.Return).Count > 1
            && name is not ("Values" or "Arguments"))
            throw new InvalidDataException("Enter one Lua expression.");
    }

    private static IEnumerable<LuaToken> Tokens(string value) =>
        LuaScript.Syntax(value).Where(token => token.Kind != LuaTokenKind.Comment);

    /** A node's body as nodes and execution edges; each statement keeps its lexical scope. */
    public (IReadOnlyList<MissionScriptNode> Nodes, IReadOnlyList<MissionScriptEdge> Edges) Body(MissionScriptNode owner)
    {
        if (bodies.TryGetValue(owner.Key, out var known)) return known;
        if (!CanOpen(owner)) return ([], []);
        var nodes = new List<MissionScriptNode>();
        var edges = new List<MissionScriptEdge>();
        if (owner.Function is { } function)
        {
            string scope = Register(owner, function.Body, "body");
            Sequence(function.Body.Statements, scope, null, null, 0, owner.Key);
        }
        else Control(owner, null, null, 0);
        var result = ((IReadOnlyList<MissionScriptNode>)nodes.OrderBy(node => node.First).ThenBy(node => node.Structural).ToArray(),
            (IReadOnlyList<MissionScriptEdge>)edges.Distinct().ToArray());
        bodies.Add(owner.Key, result);
        return result;

        string Register(MissionScriptNode node, LuaBlock body, string suffix)
        {
            string key = node.Key + "/scope/" + suffix;
            scopes[key] = new(key, body.Label, node.Key, body);
            return key;
        }

        void Edge(string from, string? to, string label)
        {
            if (to is not null) edges.Add(new(from, to, label));
        }

        string? Sequence(IReadOnlyList<LuaStatement> statements, string scope, string? next, string? exit, int depth, string? keyPrefix = null)
        {
            if (depth > 64 || nodes.Count + statements.Count > 20000)
                throw new InvalidDataException("Lua control flow exceeds the graph limit.");
            for (int i = statements.Count - 1; i >= 0; i--)
            {
                var statement = statements[i];
                var function = (statement as LuaFunctionStatement)?.Function;
                int first = TokenAt(statement.Code.Start), end = TokenAt(statement.Code.End);
                var node = Describe(new((keyPrefix ?? scope) + "/" + i, function is not null ? MissionScriptNodeKind.Function : MissionScriptNodeKind.Code,
                    StatementTitle(first, end), statement.Span, first, end, function)
                    { Scope = scope, RemoveSpan = LineSpan(statement.Span) }, statement);
                if (node.Control is not null) Control(node, next, exit, depth + 1);
                else
                {
                    nodes.Add(node);
                    if (node.Kind == MissionScriptNodeKind.Break) Edge(node.Key, exit, "break");
                    else if (node.Kind != MissionScriptNodeKind.Return)
                        Edge(node.Key, next, node.FallbackReason is null ? "next" : "source order");
                }
                next = node.Key;
            }
            return next;
        }

        void Control(MissionScriptNode node, string? next, string? exit, int depth)
        {
            nodes.Add(node);
            var block = node.Control!;
            if (block.Kind == "if")
            {
                string? otherwise = next;
                for (int i = block.Bodies.Count - 1; i >= 0; i--)
                {
                    var branch = block.Bodies[i];
                    var test = i == 0 ? node : new MissionScriptNode(node.Key + "/branch/" + i,
                        branch.Label == "else" ? MissionScriptNodeKind.Else : MissionScriptNodeKind.ElseIf,
                        branch.Label == "else" ? "else" : "elseif " + branch.Condition!.Span.Read(Source),
                        branch.Span, TokenAt(branch.Span.Start), TokenAt(branch.Closing), null)
                        { Structural = true, Scope = node.Scope, SourceIdentity = sourceIdentity,
                            Inputs = branch.Condition is { } input ? [new("Condition", input.Span)] : [],
                            ApiCalls = branch.Condition is { } owned ? OwnedCalls([owned.Span]) : [] };
                    if (i != 0) nodes.Add(test);
                    string scope = Register(node, branch, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    string? entry = Sequence(branch.Statements, scope, next, exit, depth);
                    Edge(test.Key, entry, branch.Label == "else" ? "next" : "true");
                    if (branch.Label != "else") Edge(test.Key, otherwise, "false");
                    otherwise = test.Key;
                }
            }
            else
            {
                var body = block.Bodies[0];
                string scope = Register(node, body, "body");
                if (block.Kind == "do") Edge(node.Key, Sequence(body.Statements, scope, next, exit, depth), "enter");
                else if (block.Kind == "repeat")
                {
                    var until = new MissionScriptNode(node.Key + "/until", MissionScriptNodeKind.Until,
                        "until " + block.Input!.Value.Read(Source), block.Input.Value, TokenAt(body.Closing), node.End, null)
                        { Structural = true, Scope = scope, SourceIdentity = sourceIdentity, Inputs = [new("Condition", block.Input.Value)],
                            ApiCalls = OwnedCalls([block.Input.Value]) };
                    nodes.Add(until);
                    string? entry = Sequence(body.Statements, scope, until.Key, next, depth);
                    Edge(node.Key, entry, "enter"); Edge(until.Key, entry, "false"); Edge(until.Key, next, "true");
                }
                else
                {
                    string? entry = Sequence(body.Statements, scope, node.Key, next, depth);
                    Edge(node.Key, entry, block.Kind == "for" ? "next item" : "true");
                    Edge(node.Key, next, block.Kind == "for" ? "done" : "false");
                }
            }
        }
    }
}
