using System.Text.Json;
using SunriseStudio.Missions;
using SunriseStudio.Projects;

/** Checks creation of mission flow graphs: custom conditions, installed flow helper expressions and callbacks. */
internal static class FlowCreationChecks
{
    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic()
    {
        const string source = "local flow=require('lib.flow')\nlocal graph=flow.new{key='x',facts={{id='a',observe=function()return true end},{id='b',observe=function()return true end}},steps={{id='first',run=function()local preserve=[=[untouched]=]end}}}";
        var graph = MissionGraphDocument.Parse(source); var group = graph.Groups.Single(value => value.Collection == "steps");
        string key = group.Nodes[0].Key, callback = graph.Field(key, "run")!.Value.Read(source);
        var condition = new MissionConditionGroup(false, [new MissionFactCondition("a"), new MissionConditionGroup(true,
            [new MissionFactCondition("b"), new MissionCustomCondition("function(context, state)\n -- keep this callback\n return state:variable(\"ready\") == true end")])]);
        graph = new MissionVisualAuthoring(graph, []).SetCondition(key, "await", condition);
        var parsed = MissionConditions.Read(graph.Field(key, "await")!.Value.Read(graph.Source), new(graph.Source));
        Check(MissionConditions.Facts(parsed).SequenceEqual(new[] { "a", "b" })
            && MissionConditions.Write(parsed!).Contains("-- keep this callback", StringComparison.Ordinal)
            && graph.Field(key, "run")!.Value.Read(graph.Source) == callback, "Nested conditions rewrote custom Lua.");
        Check(MissionConditions.Read("flow.any(flow.fact('a'), flow.all(flow.fact('b'), predicate))", new(source)) is MissionConditionGroup,
            "Installed flow helper expressions were not recognized.");
        Check(MissionConditions.Read("flow.fact('a') + custom()", new(source)) is MissionCustomCondition,
            "A composed Lua expression was mistaken for a plain helper call.");
        foreach (string invalid in new[] { "true", "{any={}}", "{fact='a',all={}}", "{foo=true}", "flow.all()", "{fact=''}", "flow.fact('')", "{all={nil}}" })
            Reject(() => MissionConditions.Read(invalid, new(source)), invalid);
        foreach (string expression in new[] { "{fact='missing'}", "flow.fact('missing')", "{all={{fact='a'},{fact='missing'}}}" })
            Reject(() => graph.SetField(key, "await", expression).RequireValid(), expression);
        Reject(() => graph.SetField(key, "reset_facts", "{'a'}").RequireValid());
        Reject(() => graph.SetField(key, "checkpoint", "7").RequireValid());
        Reject(() => graph.SetField(key, "run", "true").RequireValid());
        Reject(() => graph.SetField(key, "after", "'first'").RequireValid());
        Reject(() => MissionGraphDocument.Parse("local flow=require('lib.flow');return flow.new{key='empty',steps={}}").RequireValid());
        Check(MissionScriptCatalog.ControllerStem("Bug in the System (Heroic)") == "bug_in_the_system_heroic"
            && MissionScriptCatalog.ControllerStem("42_Test") == "_42_test"
            && MissionScriptCatalog.ControllerStem("---") == "unnamed", "Export filename differs from the runtime controller rule.");
        Console.WriteLine("mission_flow conditions=nested custom=preserved invalid=blocked controller_path=checked");
    }

    /** Runs the same kind of checks against the real packages of the owned installation. */
    public static void Owned(string root, string game)
    {
        var catalog = new MissionScriptCatalog(game);
        var activity = catalog.Activities.Single(value => value.Key == "MISSION_LIGHTS");
        var entry = catalog.EntryForActivity(activity); var refs = catalog.ReadReferences(entry);
        var initial = refs.First(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.State));
        var draft = MissionScriptFactory.CreateFlow(catalog, activity, initial, "studio_branches");
        var graph = draft.Graph;
        Check(entry.Name == "mission_lights" && entry.SdkModule == "missions.mission_lights_80b3ed9a"
            && graph.Source.Contains("on_event_client_state_changed", StringComparison.Ordinal), "Starter activity binding or arrival callback differs.");
        Reject(() => MissionScriptFactory.CreateFlow(catalog, activity, initial, new string('x', 44)));
        string steps = graph.Groups.Single(value => value.Collection == "steps").Key;
        string facts = graph.Groups.Single(value => value.Collection == "facts").Key;
        var triggers = refs.Where(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.Trigger)).DistinctBy(value => value.Identity).Take(2).ToArray();
        var directives = refs.Where(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.Directive)).DistinctBy(value => value.Identity).Take(2).ToArray();
        var sensor = refs.First(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.DirectiveSensor));
        string untouched = graph.Field(steps + "/start", "run")!.Value.Read(graph.Source);
        for (int i = 0; i < 2; i++)
        {
            string name = i == 0 ? "left" : "right";
            graph = new MissionVisualAuthoring(graph, refs).CreateNode(facts, name, MissionNodeTemplate.TriggerFact, triggers[i]);
            graph = new MissionVisualAuthoring(graph, refs).CreateNode(steps, "arm_" + name, MissionNodeTemplate.FlowArmTrigger, triggers[i]);
            graph = graph.Connect(steps + "/start", steps + "/arm_" + name);
        }
        graph = new MissionVisualAuthoring(graph, refs).CreateNode(steps, "either", MissionNodeTemplate.FlowObjective, directives[0], sensor: sensor);
        graph = graph.Connect(steps + "/arm_left", steps + "/either").Connect(steps + "/arm_right", steps + "/either");
        graph = new MissionVisualAuthoring(graph, refs).SetCondition(steps + "/either", "when",
            new MissionConditionGroup(false, [new MissionFactCondition("left"), new MissionFactCondition("right")]));
        graph = new MissionVisualAuthoring(graph, refs).SetFlowAction(steps + "/either", new(MissionNodeTemplate.FlowObjective, directives[1], sensor));
        Check(new MissionVisualAuthoring(graph, refs).ReadFlowAction(steps + "/either")?.Target == directives[1], "Typed action change did not retain its new SDK target.");
        graph = new MissionVisualAuthoring(graph, refs).CreateNode(steps, "both", MissionNodeTemplate.FlowCompleteMission);
        graph = graph.Connect(steps + "/either", steps + "/both");
        graph = new MissionVisualAuthoring(graph, refs).SetCondition(steps + "/both", "when",
            new MissionConditionGroup(true, [new MissionFactCondition("left"), new MissionFactCondition("right")]));
        Check(graph.Field(steps + "/start", "run")!.Value.Read(graph.Source) == untouched, "Editing actions changed another callback.");
        var custom = graph.SetField(steps + "/start", "run", "function(context, state) context:clear_variable('custom') end");
        Reject(() => new MissionVisualAuthoring(custom, refs).SetFlowAction(steps + "/start", new(MissionNodeTemplate.FlowCompleteMission)));
        var project = StudioProject.Create(Path.Combine(root, "new-flow-project"), "New mission behavior");
        MissionProject.Save(project, entry.RelativePath, graph.Source);
        var reopened = StudioProject.Open(project.DirectoryPath);
        var listed = MissionProject.List(reopened).Single();
        var restored = catalog.Describe(listed.Name, listed.RelativePath, listed.FullPath);
        string saved = MissionScriptCatalog.ReadSource(restored.FullPath);
        Check(saved == graph.Source && restored.SdkModule == entry.SdkModule && catalog.ReadReferences(restored).Count == refs.Count
            && catalog.DescribeSource(entry.Name, entry.RelativePath, "-- require(missions.WRONG)\n" + graph.Source).SdkModule == entry.SdkModule,
            "Project reopen or a comment selected the wrong SDK module.");
        Check(reopened.Changes.Count == 0 && reopened.AddedAssets.Count == 0, "New mission source was saved as a package edit.");
        string export = MissionProject.Export(restored.RelativePath, saved, Path.Combine(root, "new-flow-export"), game);
        File.WriteAllText(Path.Combine(root, "new-flow-runtime.json"), JsonSerializer.Serialize(new
        { Source = export, TriggerA = triggers[0].Identity, TriggerB = triggers[1].Identity, Directive = directives[1].Identity }));
        Reject(() => MissionProject.Export(restored.RelativePath, saved, Path.Combine(game, "bin/x64/Sunrise/scripts"), game));
        Console.WriteLine($"mission_flow_new create=pass branches=all_any action_edit=pass reopen=pass export={export} runtime={Path.Combine(root, "new-flow-runtime.json")}");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action, string? input = null)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or NotSupportedException or InvalidOperationException) { return; }
        throw new InvalidDataException("Invalid flow edit was accepted" + (input is null ? "." : ": " + input));
    }
}
