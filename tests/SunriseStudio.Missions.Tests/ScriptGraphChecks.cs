using SunriseStudio.Missions;

/** Checks the Lua script to graph reader and writer: helpers, headers and comments round-trip. */
internal static class ScriptGraphChecks
{
    public static int Nodes { get; private set; }
    public static int Bodies { get; private set; }

    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic()
    {
        const string source = "-- header\nlocal flow = require(\"lib.flow\")\nlocal x = 1.5e-3 .. \"a\" -- tail\n\n"
            + "-- helper\nlocal function helper(context)\n    if x == 1 then return end\n    context:set_variable(\"a\", true)\nend\n"
            + "local graph = flow.new{key=\"k\", steps={{id=\"a\"}}}\nreturn {\n    -- start\n"
            + "    on_start = function(context, state) graph:advance(context, state) end,\n    on_event_x = helper,\n"
            + "    initial_state = {region_index = 1},\n}\n";
        var document = MissionGraphDocument.Parse(source);
        var view = MissionScriptGraph.Parse(source, document.Groups, ApiNodeChecks.Context());
        Check(view.Nodes.Select(node => node.Kind).SequenceEqual([MissionScriptNodeKind.Module, MissionScriptNodeKind.Assignment,
            MissionScriptNodeKind.Function, MissionScriptNodeKind.Flow, MissionScriptNodeKind.Callback, MissionScriptNodeKind.Callback,
            MissionScriptNodeKind.Setting]), "Script statements were classified wrongly.");
        var helper = view.Nodes[2]; var start = view.Nodes[4];
        Check(view.Code(helper).StartsWith("-- helper\n", StringComparison.Ordinal) && view.Code(view.Nodes[1]) == "local x = 1.5e-3 .. \"a\""
            && view.Code(start).StartsWith("-- start\n", StringComparison.Ordinal), "Leading comments did not stay with their statement.");
        var links = view.Edges.Select(edge => (view.Node(edge.Source).Kind, view.Node(edge.Target).Kind, edge.Label)).ToHashSet();
        Check(links.SetEquals(new[] { (MissionScriptNodeKind.Assignment, MissionScriptNodeKind.Function, "x"),
            (MissionScriptNodeKind.Module, MissionScriptNodeKind.Flow, "flow"), (MissionScriptNodeKind.Flow, MissionScriptNodeKind.Callback, "graph"),
            (MissionScriptNodeKind.Function, MissionScriptNodeKind.Callback, "helper") }), "Definitions did not link to their uses.");
        var body = view.Body(helper);
        Check(body.Nodes.Count == 3 && body.Edges.Count == 2 && body.Nodes[^1] is { Title: "Set variable", Kind: MissionScriptNodeKind.ApiCall }
            && view.Body(start).Nodes.Single().Title == "Advance flow", "A function body did not open as its statements.");
        Check(MissionScriptGraph.Parse(source, document.Groups).Body(helper).Nodes[^1].Kind == MissionScriptNodeKind.Call,
            "Without a schema a call did not stay as before.");
        string replaced = view.Replace(helper, "local function helper(context)\n    context:complete_mission{}\nend");
        Check(replaced.Contains("context:complete_mission{}", StringComparison.Ordinal) && !replaced.Contains("-- helper", StringComparison.Ordinal)
            && replaced.EndsWith("    initial_state = {region_index = 1},\n}\n", StringComparison.Ordinal), "Replacing a node touched other code.");
        Reject(() => view.Replace(helper, "local function helper("));
        Check(!view.Remove(view.Nodes[1]).Contains("local x", StringComparison.Ordinal), "Removing a node left its code.");
        Check(view.Move(view.Nodes, helper.Key, -1).IndexOf("-- helper", StringComparison.Ordinal)
            < view.Move(view.Nodes, helper.Key, -1).IndexOf("local x", StringComparison.Ordinal), "Moving a node did not swap it.");
        Check(view.Move(view.Nodes, start.Key, -1) == source, "A callback swapped places with a statement.");
        var added = MissionScriptGraph.Parse(view.AddStatement("local function extra(context)\nend"), []);
        Check(added.Nodes.Any(node => node.Key == "script/function/extra") && added.Nodes[^1].Kind == MissionScriptNodeKind.Setting,
            "A new statement was not placed before the returned table.");
        var withField = MissionScriptGraph.Parse(view.AddField("on_event_y = function(context, state, event)\nend"), []);
        Check(withField.Nodes.Any(node => node.Key == "script/field/on_event_y"), "A new callback was not added to the returned table.");
        var grown = MissionScriptGraph.Parse(view.AddToBody(helper, "context:clear_variable(\"a\")"), []);
        Check(grown.Body(grown.Node(helper.Key)).Nodes.Count == 4, "A statement was not added to the function body.");
        Console.WriteLine("script_graph kinds=pass comments=attached links=pass bodies=pass edits=exact");
    }

    /** Every installed script reads as a script graph, and replacing each node with its own code changes nothing. */
    public static void Installed(string name, MissionGraphDocument document)
    {
        MissionScriptGraph view;
        try { view = MissionScriptGraph.Parse(document.Source, document.Groups); }
        catch (InvalidDataException error) { throw new InvalidDataException($"{name}: {error.Message}", error); }
        Check(view.Nodes.Count > 0, name + " has no script nodes.");
        int previous = 0;
        foreach (var node in view.Nodes)
        {
            Check(node.Span.Start >= previous, $"{name}: node {node.Key} overlaps the one before it.");
            previous = node.Span.End;
            Check(view.Replace(node, view.Code(node)) == document.Source, $"{name}: an unchanged node did not round-trip.");
            if (node.Function is null) continue;
            var body = view.Body(node);
            Bodies++;
            foreach (var statement in body.Nodes)
                Check(view.Replace(statement, view.Code(statement)) == document.Source, $"{name}: an unchanged statement did not round-trip.");
        }
        Nodes += view.Nodes.Count;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidDataException("An invalid script edit was accepted.");
    }
}
