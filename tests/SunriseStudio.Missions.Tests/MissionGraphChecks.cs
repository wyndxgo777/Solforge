using System.Text;
using SunriseStudio.Missions;
using SunriseStudio.Projects;

/** The flow and campaign graph view: span-exact edits, mod and installed saves, and every installed script read. */
internal static class MissionGraphChecks
{
    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic(string temporary)
    {
        string source = "﻿-- preserve this\r\nlocal flow = require(\"lib.flow\")\r\n"
            + "local untouched = [=[, } end function]=]\r\n"
            + "local graph = flow.new{key=\"test\", facts={}, steps={\r\n"
            + " {id=\"a\", run=function(context) for i=1,3 do if i>1 then context:clear_variable(\"x\") end end end},\r\n"
            + " {id=\"b\", after={\"a\"}, await=function() repeat local x=1 until x>0 return true end},\r\n"
            + "}}\r\nreturn {on_start=function(c,s) graph:advance(c,s) end}\r\n";
        var graph = MissionGraphDocument.Parse(source);
        Check(graph.Source == source && graph.Nodes.Count == 2 && graph.Edges.Count == 1, "Flow import changed source or missed nodes.");
        var stepGroup = graph.Groups.Single(group => group.Collection == "steps");
        string a = stepGroup.Nodes[0].Key, b = stepGroup.Nodes[1].Key;
        graph.RequireValid();
        var changed = graph.SetField(a, "checkpoint", "true");
        Check(changed.Source.Contains("local untouched = [=[, } end function]=]", StringComparison.Ordinal)
            && changed.Node(a).Table!.Field("run")!.Value.Read(changed.Source) == graph.Node(a).Table!.Field("run")!.Value.Read(source),
            "Field editing rewrote unhandled Lua.");
        Reject(() => graph.Connect(b, a));
        var disconnected = graph.Connect(a, b, false);
        Check(disconnected.Edges.Count == 0, "Disconnect retained a dependency.");
        var added = graph.AddNode(stepGroup.Key, "c");
        added = added.Connect(b, stepGroup.Key + "/c");
        Check(added.Nodes.Count == 3 && added.Edges.Count == 2, "A new flow node was not connected.");
        Reject(() => added.RemoveNode(b));
        Check(added.RemoveNode(stepGroup.Key + "/c").Nodes.Count == 2, "Leaf removal failed.");
        Reject(() => graph.SetField(a, "run", "function() return true"));
        Reject(() => graph.SetField(a, "run", "true, injected = true"));
        var supplied = MissionGraphDocument.Parse("local flow = require(\"lib.flow\")\n"
            + "local graph = flow.new{key=\"k\", steps={make(\"a\"), {id=\"b\"}}}\n");
        var suppliedNode = supplied.Groups.Single(group => group.Collection == "steps").Nodes[0];
        var replaced = supplied.SetEntry(suppliedNode.Key, "{id = \"a\", run = function(context, state) end}");
        Check(suppliedNode.Table is null && replaced.Nodes.Any(node => node.Id == "a" && node.Table is not null)
            && replaced.Source.Contains("{id=\"b\"}", StringComparison.Ordinal), "A Lua-supplied node was not replaced in place.");
        Reject(() => supplied.SetEntry(suppliedNode.Key, "make(\"a\"), injected"));
        string campaign = "local campaign = require('lib.campaign')\nlocal guard={id='guard',after='first',custom=[=[x]=]}\n"
            + "return campaign.new{key='story',legs={{id='leg',state=mission.states.X}},steps={{id='first',ends={trigger=mission.Slot.X}},{id='second'}},encounters={guard}}";
        var story = MissionGraphDocument.Parse(campaign);
        var chain = story.Groups.Single(group => group.Collection == "steps");
        Check(story.Edges.Any(edge => edge.Kind == "Next") && story.Edges.Any(edge => edge.Kind == "Started"),
            "Campaign order or encounter ownership was lost.");
        var moved = story.MoveNode(chain.Nodes[1].Key, -1);
        Check(moved.Groups.Single(group => group.Collection == "steps").Nodes[0].Id == "second", "Campaign ordering did not change.");
        Reject(() => story.Connect(chain.Nodes[0].Key, chain.Nodes[1].Key));
        var nested = story.SetField(chain.Nodes[1].Key, "ends.trigger", "mission.Slot.OTHER");
        Check(nested.Node(chain.Nodes[1].Key).Table!.Field("ends")!.Table!.Field("trigger")!.Value.Read(nested.Source) == "mission.Slot.OTHER",
            "Nested field was not authored.");
        // A mod script is a plain file in the project, laid out as the mod installs it; never a package edit.
        var project = StudioProject.Create(Path.Combine(temporary, "project"), "Mission graph");
        string file = MissionProject.Save(project, "test/test.lua", source);
        Check(file == Path.Combine(project.DirectoryPath, "scripts", "test", "test.lua")
            && File.ReadAllBytes(file).SequenceEqual(Encoding.UTF8.GetBytes(source)), "A mod script was not written as its exact file.");
        project = StudioProject.Open(project.DirectoryPath);
        Check(project.Changes.Count == 0 && project.AddedAssets.Count == 0, "A mod script entered the project's package edits.");
        Check(MissionProject.List(project).Single().RelativePath == "test/test.lua", "Project reopen lost its mod script.");
        var modScript = MissionProject.ModScripts(project).Single();
        Check(modScript.RelativePath == "test/test.lua" && modScript.Data.SequenceEqual(Encoding.UTF8.GetBytes(source)),
            "Mod install changed the script path, BOM or line endings.");
        foreach (string escape in new[] { "../escape/escape.lua", "test/other.lua", "a/b/b.lua", "te:st/te:st.lua" })
            Reject(() => MissionProject.Save(project, escape, source), escape);
        // An installed script saves in place and belongs to no project.
        string game = Path.Combine(temporary, "installation");
        string installedFile = Path.Combine(game, "bin", "x64", "Sunrise", "scripts", "test", "test.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(installedFile)!); File.WriteAllText(installedFile, "-- old\n");
        var installed = new MissionScriptCatalog(game);
        Check(installed.Scripts.Single().RelativePath == "test/test.lua", "The installed script was not listed.");
        Check(installed.SaveInstalled("test/test.lua", source) == Path.GetFullPath(installedFile)
            && File.ReadAllBytes(installedFile).SequenceEqual(Encoding.UTF8.GetBytes(source)), "An installed script did not save in place.");
        Reject(() => installed.SaveInstalled("../../escape/escape.lua", source));
        string export = MissionProject.Export("test/test.lua", source, Path.Combine(temporary, "review"), game);
        Check(File.ReadAllBytes(export).SequenceEqual(Encoding.UTF8.GetBytes(source)), "Export changed BOM or line endings.");
        Reject(() => MissionProject.Export("test/test.lua", source, game, game));
        Console.WriteLine("mission_graph source=lossless flow=validated campaign=ordered mod_script=file installed=in_place export=exact");
    }

    /** A node added to an empty one-line table goes on its own line, indented and broken like the file. */
    public static void EmptyTables()
    {
        foreach (var (unit, newline) in new[] { ("    ", "\n"), ("\t", "\r\n") })
        {
            string Lines(params string[] lines) => string.Join(newline, lines) + newline;
            string source = Lines("local flow = require(\"lib.flow\")", "local graph = flow.new{", unit + "key = \"k\",",
                unit + "facts = {},", unit + "steps = {},", "}", "return {}");
            var graph = MissionGraphDocument.Parse(source);
            graph = graph.AddNode(graph.Groups.Single(group => group.IsFlowSteps).Key, "first");
            graph = graph.AddNode(graph.Groups.Single(group => group.IsFlowFacts).Key, "seen");
            string expected = Lines("local flow = require(\"lib.flow\")", "local graph = flow.new{", unit + "key = \"k\",",
                unit + "facts = {",
                unit + unit + "{ id = \"seen\", observe = function(context, state, event) return false end },",
                unit + "},",
                unit + "steps = {",
                unit + unit + "{ id = \"first\", run = function(context, state) end },",
                unit + "},",
                "}", "return {}");
            Check(graph.Source == expected, $"A node in an empty table was laid out wrong:\n{graph.Source}");
        }
        Console.WriteLine("mission_graph empty_table_entry=own_line indent=spaces,tabs newline=lf,crlf");
    }

    /**
     * Every installed script reads as a graph without change, then a Mission Lights step takes a field edit.
     * @param references SDK references by script name.
     */
    public static void Installed(MissionScriptCatalog catalog, IReadOnlyDictionary<string, IReadOnlyList<MissionSdkReference>> references, string temporary)
    {
        int graphs = 0, nodes = 0, installedReferences = 0;
        foreach (var entry in catalog.Scripts)
        {
            string original = MissionScriptCatalog.ReadSource(entry.FullPath);
            var parsed = MissionGraphDocument.Parse(original);
            Check(parsed.Source == original, "Installed source was rewritten.");
            graphs += parsed.Groups.Count; nodes += parsed.Nodes.Count;
            installedReferences += references[entry.Name].Count;
            ScriptGraphChecks.Installed(entry.Name, parsed);
            ScriptControlChecks.Installed(entry.Name, parsed, Path.Combine(temporary, "script-control-installed"));
        }
        Console.WriteLine($"script_graph installed={catalog.Scripts.Count} nodes={ScriptGraphChecks.Nodes} bodies={ScriptGraphChecks.Bodies} identity_edits=pass");
        var lights = catalog.Scripts.Single(entry => entry.Name == "mission_lights");
        var actual = MissionGraphDocument.Parse(MissionScriptCatalog.ReadSource(lights.FullPath));
        var actualGroup = actual.Groups.Single(group => group.Collection == "steps");
        Check(actualGroup.Nodes.Count > 0 && actualGroup.Nodes.All(node => node.Table?.Field("id")?.StringValue is not null),
            "Mission Lights steps did not read as tables with ids.");
        var sdk = catalog.ReadReferences(lights);
        Check(sdk.Count > 100 && sdk.Any(value => value.Category == "states"), "Installed SDK references were not decoded.");
        var edited = actual.SetField(actualGroup.Nodes[0].Key, "navpoint", "mission.Slot.NAV_POINT_80B99468");
        string destination = Path.Combine(temporary, "installed-edited.lua"); File.WriteAllText(destination, edited.Source);
        Console.WriteLine($"mission_graph installed_scripts={catalog.Scripts.Count} groups={graphs} nodes={nodes} sdk_references={sdk.Count} installed_references={installedReferences} edited={destination}");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject(Action action, string? input = null)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or NotSupportedException) { return; }
        throw new InvalidDataException("Invalid graph operation was accepted" + (input is null ? "." : ": " + input));
    }
}
