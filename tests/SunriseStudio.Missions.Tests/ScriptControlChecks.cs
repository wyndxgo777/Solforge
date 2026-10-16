using SunriseStudio.Missions;

/** Checks Lua control-flow parsing and rewriting: comments, branches and loops are kept verbatim. */
internal static class ScriptControlChecks
{
    private const string Fixture = "\uFEFF-- controller\r\nlocal function run(ctx, xs)\r\n"
        + "    local total = 0 -- keep assignment tail\r\n"
        + "    for i = 1, 3 do\r\n"
        + "        -- keep branch\r\n        if i == 2 then\r\n            ctx:skip(i)\r\n"
        + "        elseif i < 0 then\r\n            break\r\n"
        + "        else\r\n            total = total + i\r\n        end\r\n    end\r\n"
        + "    while total < 6 do total = total + 1 end\r\n"
        + "    repeat total = total - 1 until total == 5\r\n"
        + "    do local hidden = total ctx:record(hidden) end\r\n"
        + "    return total\r\nend\r\nreturn {on_start = run}\r\n";

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string temporary)
    {
        var graph = MissionScriptGraph.Parse(Fixture);
        var owner = graph.Nodes.Single(node => node.Function is not null);
        var body = graph.Body(owner);
        Check(body.Nodes.First().Key == owner.Key + "/0", "Existing direct body node layout keys changed.");
        foreach (var kind in new[] { MissionScriptNodeKind.If, MissionScriptNodeKind.ElseIf, MissionScriptNodeKind.Else,
            MissionScriptNodeKind.For, MissionScriptNodeKind.While, MissionScriptNodeKind.Repeat, MissionScriptNodeKind.Until,
            MissionScriptNodeKind.Scope, MissionScriptNodeKind.Call, MissionScriptNodeKind.Assignment, MissionScriptNodeKind.Return,
            MissionScriptNodeKind.Break })
            Check(body.Nodes.Any(node => node.Kind == kind), "Control-flow kind missing: " + kind);
        Check(body.Nodes.All(node => node.Kind != MissionScriptNodeKind.Code), "Basic Lua was left in a code block.");
        var loop = body.Nodes.Single(node => node.Kind == MissionScriptNodeKind.For);
        var condition = body.Nodes.Single(node => node.Kind == MissionScriptNodeKind.If);
        var repeat = body.Nodes.Single(node => node.Kind == MissionScriptNodeKind.Repeat);
        var until = body.Nodes.Single(node => node.Kind == MissionScriptNodeKind.Until);
        var @while = body.Nodes.Single(node => node.Kind == MissionScriptNodeKind.While);
        var @break = body.Nodes.Single(node => node.Kind == MissionScriptNodeKind.Break);
        var skip = body.Nodes.Single(node => node.Kind == MissionScriptNodeKind.Call && graph.Code(node).Contains(":skip", StringComparison.Ordinal));
        var record = body.Nodes.Single(node => node.Kind == MissionScriptNodeKind.Call && graph.Code(node).Contains(":record", StringComparison.Ordinal));
        Check(body.Edges.Any(edge => edge.Source == condition.Key && edge.Target == skip.Key && edge.Label == "true")
            && body.Edges.Any(edge => edge.Source == @break.Key && edge.Target == @while.Key && edge.Label == "break")
            && body.Edges.Any(edge => edge.Source == until.Key && edge.Label == "false")
            && !body.Edges.Any(edge => body.Nodes.Single(node => node.Key == edge.Source).Kind == MissionScriptNodeKind.Return),
            "Branch, break, loop or return edges do not match execution.");
        foreach (var node in body.Nodes)
            Check(graph.Replace(node, graph.Code(node)) == Fixture, "No-op body edit changed source.");
        Check(graph.Move(body.Nodes, skip.Key, 1) == Fixture, "Reordering crossed a branch scope.");
        Check(graph.CanBreak(graph.Scopes(condition)[0].Key) && !graph.CanBreak(graph.Scopes(owner)[0].Key),
            "Break admission crossed its lexical loop.");
        string edited = graph.SetInput(loop, "Loop", "i = 1, 4");
        edited = Edit(edited, condition.Key, "Condition", "i == 3");
        edited = Edit(edited, record.Key, "Arguments", "\"changed\", hidden");
        edited = Edit(edited, record.Key, "Call", "ctx:mark");
        edited = Edit(edited, until.Key, "Condition", "total <= 5");
        Check(edited.StartsWith("\uFEFF-- controller\r\n", StringComparison.Ordinal)
            && edited.Contains("-- keep assignment tail\r\n", StringComparison.Ordinal)
            && edited.Contains("-- keep branch\r\n", StringComparison.Ordinal)
            && !edited.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n'),
            "Typed edits changed comments, BOM or line endings.");
        Reject(() => graph.SetInput(condition, "Condition", "true then end error('escape') if true"));
        Reject(() => graph.SetInput(skip, "Arguments", "1); injected(); call("));
        Reject(() => graph.SetInput(skip, "Call", "ctx:skip(1)"));
        Reject(() => graph.SetInput(condition, "Condition", "(true]"));
        Reject(() => graph.SetInput(skip, "Arguments", "{value=}"));
        foreach (string invalid in new[] { "helper(;)", "state[;]", "(true false)", "helper(1,)" })
            Reject(() => graph.SetInput(skip, "Arguments", invalid), invalid);
        foreach (string invalid in new[] { ".", "1e", "0xGG", "1..2" })
            Reject(() => graph.SetInput(skip, "Arguments", invalid), invalid);
        Reject(() => graph.SetInput(skip, "Arguments", new string('{', 300) + "1" + new string('}', 300)));
        Reject(() => graph.SetInput(loop, "Loop", "i = 1"));
        Reject(() => graph.SetInput(loop, "Loop", "i=1,2 do end for j=1,2"));
        Reject(() => graph.SetInput(loop, "Loop", "i=1,2,3,4"));
        Reject(() => MissionScriptGraph.Parse(Fixture).SetInput(condition, "Condition", "true"));
        var foreign = MissionScriptGraph.Parse(Fixture);
        Reject(() => foreign.AddBranch(condition, "true"));
        Reject(() => foreign.AddToBody(owner, "ctx:extra()"));
        Reject(() => foreign.Remove(skip));
        Reject(() => foreign.Replace(skip, "ctx:extra()"));
        Reject(() => foreign.Move(body.Nodes, skip.Key, 1));
        Reject(() => graph.AddToScope(graph.Scopes(owner)[0].Key, "break"));
        string grown = graph.AddToBody(owner, "ctx:extra()");
        Check(grown.IndexOf("ctx:extra()", StringComparison.Ordinal) < grown.IndexOf("return total", StringComparison.Ordinal),
            "Insertion placed code after a terminal return.");
        string branch = graph.AddBranch(condition, "i == 99");
        var branchView = MissionScriptGraph.Parse(branch);
        Check(branchView.Node(condition.Key).Control!.Bodies.Count == 4
            && branch.Contains("else\r\n            total = total + i", StringComparison.Ordinal),
            "Adding an else-if changed existing branches.");
        Reject(() => graph.AddBranch(condition, null));
        var elseIf = body.Nodes.Single(node => node.Kind == MissionScriptNodeKind.ElseIf);
        string removedBranch = graph.RemoveBranch(elseIf);
        Check(!removedBranch.Contains("elseif", StringComparison.Ordinal) && !removedBranch.Contains("break", StringComparison.Ordinal)
            && removedBranch.Contains("else\r\n            total = total + i", StringComparison.Ordinal),
            "Removing an else-if changed its sibling or closing block.");
        Reject(() => foreign.RemoveBranch(elseIf));
        var inline = MissionScriptGraph.Parse("local function f() if true then x=value else other() end end");
        var inlineElse = inline.Body(inline.Nodes.Single()).Nodes.Single(node => node.Kind == MissionScriptNodeKind.Else);
        Check(inline.RemoveBranch(inlineElse).Contains("x=value end", StringComparison.Ordinal),
            "Removing an inline branch joined Lua tokens.");
        var scope = graph.Scopes(condition)[0];
        string scoped = graph.AddToScope(scope.Key, "ctx:extra()");
        Check(scoped.IndexOf("ctx:extra()", StringComparison.Ordinal) < scoped.IndexOf("elseif", StringComparison.Ordinal),
            "Scoped insertion escaped into another branch.");
        string broken = graph.AddToScope(graph.Scopes(loop)[0].Key, "break");
        var iterator = MissionScriptGraph.Parse("local function run() local total=0 for index,value in ipairs({2,3}) do total=total+value end return total end return {on_start=run}");
        var iteratorLoop = iterator.Body(iterator.Nodes[0]).Nodes.Single(node => node.Kind == MissionScriptNodeKind.For);
        string iterated = iterator.SetInput(iteratorLoop, "Loop", "index,value in ipairs({1,4})");
        Check(iterated.Contains("for index,value in ipairs({1,4}) do total=total+value", StringComparison.Ordinal),
            "Iterator header edit rewrote its scoped body.");
        var outerReturn = body.Nodes.Single(node => node.Kind == MissionScriptNodeKind.Return);
        string returned = graph.SetInput(outerReturn, "Values", "total + 1");
        var declaration = body.Nodes.First(node => node.Kind == MissionScriptNodeKind.Assignment);
        string assigned = graph.SetInput(declaration, "Values", "0, 99");
        assigned = Edit(assigned, declaration.Key, "Names", "total, spare");
        Reject(() => graph.Move(body.Nodes, outerReturn.Key, -1));
        Check(!graph.Remove(skip).Contains("ctx:skip", StringComparison.Ordinal)
            && graph.Remove(skip).Contains("-- keep branch", StringComparison.Ordinal), "Removing a call changed its owner condition.");
        var empty = MissionScriptGraph.Parse("local function f() return end\n");
        var emptyReturn = empty.Body(empty.Nodes.Single()).Nodes.Single();
        Check(empty.SetInput(emptyReturn, "Values", "3").Contains("return 3 end", StringComparison.Ordinal),
            "An empty return input joined the closing keyword.");
        var literal = MissionScriptGraph.Parse("local function f() print 'before' end");
        var call = literal.Body(literal.Nodes.Single()).Nodes.Single();
        Check(literal.SetInput(call, "Arguments", "'after', 2").Contains("print ('after', 2)", StringComparison.Ordinal),
            "Literal call arguments were not converted to a valid argument list.");
        var complex = MissionScriptGraph.Parse("local function f() local t={} t[1]=2 factory()(t) ::again:: goto again end");
        var complexBody = complex.Body(complex.Nodes.Single()).Nodes;
        Check(complexBody.Count(node => node.Kind == MissionScriptNodeKind.Code) == 4
            && complexBody.Any(node => node.Kind == MissionScriptNodeKind.Assignment),
            "Complex fallback swallowed supported siblings.");
        var nested = MissionScriptGraph.Parse("local function f() local function inner() return 2 end return inner() end");
        var inner = nested.Body(nested.Nodes.Single()).Nodes.Single(node => node.Function is not null);
        Check(nested.Node(inner.Key) == inner && nested.Body(inner).Nodes.Single().Kind == MissionScriptNodeKind.Return,
            "Nested function scope cannot be opened.");
        const string flowSource = "local flow=require('lib.flow')\nlocal graph=flow.new{key='k',facts={{id='ready',observe=function(c,s) if s.ready then return true else return false end end}},steps={{id='go',run=function(c,s) while s.ready do c:advance(s) break end end}}}";
        var document = MissionGraphDocument.Parse(flowSource);
        var flow = MissionScriptGraph.Parse(flowSource, document.Groups);
        Check(flow.Callbacks.Select(node => node.CallbackPath).Order().SequenceEqual(new[] { "observe", "run" }),
            "Flow callback fields were not exposed as graphs.");
        var run = flow.Callbacks.Single(node => node.CallbackPath == "run");
        var runLoop = flow.Body(run).Nodes.Single(node => node.Kind == MissionScriptNodeKind.While);
        string flowEdit = flow.SetInput(runLoop, "Condition", "s.enabled");
        Check(flowEdit.Contains("while s.enabled do c:advance(s) break end", StringComparison.Ordinal)
            && flowEdit.Contains("observe=function(c,s) if s.ready then", StringComparison.Ordinal),
            "Callback input edit rewrote another field.");
        string folder = Path.Combine(temporary, "script-control"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "before.lua"), Fixture);
        File.WriteAllText(Path.Combine(folder, "after.lua"), edited);
        File.WriteAllText(Path.Combine(folder, "added.lua"), grown);
        File.WriteAllText(Path.Combine(folder, "branch.lua"), branch);
        File.WriteAllText(Path.Combine(folder, "scope.lua"), scoped);
        File.WriteAllText(Path.Combine(folder, "removed-branch.lua"), removedBranch);
        File.WriteAllText(Path.Combine(folder, "break.lua"), broken);
        File.WriteAllText(Path.Combine(folder, "iterator.lua"), iterated);
        File.WriteAllText(Path.Combine(folder, "return.lua"), returned);
        File.WriteAllText(Path.Combine(folder, "assignment.lua"), assigned);
        Console.WriteLine($"mission_script_control inputs=pass scopes=pass branches=pass loops=pass comments=exact fixtures={folder}");
    }

    /** Runs the checks that need an installed game; takes the installed data under test. */
    public static void Installed(string name, MissionGraphDocument document, string output)
    {
        var graph = MissionScriptGraph.Parse(document.Source, document.Groups);
        bool edited = false;
        foreach (var owner in graph.Nodes.Concat(graph.Callbacks).Where(graph.CanOpen)) Visit(owner, 0);
        void Visit(MissionScriptNode owner, int depth)
        {
            Check(depth <= 64, name + ": nested function limit");
            foreach (var node in graph.Body(owner).Nodes)
            {
                foreach (var input in node.Inputs)
                {
                    Check(graph.SetInput(node, input.Name, input.Span.Read(document.Source)) == document.Source,
                        name + ": typed no-op changed source");
                    if (!edited && input.Name == "Condition")
                    {
                        string changed = graph.SetInput(node, input.Name, "not (" + input.Span.Read(document.Source) + ")");
                        Check(changed.StartsWith(document.Source[..input.Span.Start], StringComparison.Ordinal)
                            && changed.EndsWith(document.Source[input.Span.End..], StringComparison.Ordinal),
                            name + ": real callback edit changed unrelated source");
                        Directory.CreateDirectory(output);
                        File.WriteAllText(Path.Combine(output, Path.GetFileName(name) + ".lua"), changed);
                        edited = true;
                    }
                }
                if (node.Function is not null && node.Key != owner.Key) Visit(node, depth + 1);
            }
        }
    }

    private static string Edit(string source, string key, string input, string value)
    { var next = MissionScriptGraph.Parse(source); return next.SetInput(next.Node(key), input, value); }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject(Action action, string? input = null)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidDataException("Invalid typed script edit was accepted" + (input is null ? "." : ": " + input));
    }
}
