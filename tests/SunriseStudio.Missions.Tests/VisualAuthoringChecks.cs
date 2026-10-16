using SunriseStudio.Missions;
using SunriseStudio.Projects;

/** Checks visual mission authoring: installed aliases resolve and custom mission or slot assignments are kept. */
internal static class VisualAuthoringChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static string Run(string root, string gameRoot)
    {
        var catalog = new MissionScriptCatalog(gameRoot);
        var script = catalog.Scripts.Single(value => value.Name == "mission_lights");
        string original = MissionScriptCatalog.ReadSource(script.FullPath);
        var references = catalog.ReadReferences(script);
        var graph = MissionGraphDocument.Parse(original);
        var groups = graph.Groups;
        var steps = groups.Single(group => group.Collection == "steps");
        var legs = groups.Single(group => group.Collection == "legs");
        var visual = new MissionVisualAuthoring(graph, references);
        Check(visual.Bindings.MissionVariable == "mission" && visual.Bindings.Expand("Slot.NAV_POINT1") == "mission.Slot.NAV_POINT1"
            && visual.Bindings.Expand("line") == "campaign.line", "Installed aliases were not resolved.");
        foreach (string mutation in new[] { "\nmission = custom\n", "\nlocal mission = custom\n", "\nSlot = custom\n",
            "\nlocal Slot = custom\n", "\nmission.Slot = custom\n", "\nmission.Slot, t[1] = custom, custom\n" })
        {
            var changedBindings = new MissionScriptBindings(original.Insert(original.IndexOf("return campaign.new", StringComparison.Ordinal), mutation));
            Check(changedBindings.MissionVariable is null, "A reassigned SDK binding remained available for typed generation.");
        }
        Check(new MissionScriptBindings(original.Insert(original.IndexOf("return campaign.new", StringComparison.Ordinal),
            "\nlocal same = mission == mission\n")).MissionVariable == "mission",
            "An equality comparison was treated as an SDK reassignment.");
        var directive = references.Single(value => value.Expression == "mission.Directive.RESTORE_POWER_TO_THE_STATION");
        var navpoint = references.Single(value => value.Expression == "mission.Slot.NAV_POINT1");
        var trigger = references.Single(value => value.Expression == "mission.Slot.PLAYER_TRIGGER_80B9904B");
        var cue = references.Single(value => value.Expression == "mission.DialogueCue.M_DIALOG_SENSOR.CUE_25");
        Check(trigger.SlotType == 31 && navpoint.SlotType == 47 && visual.Choices(MissionReferenceKind.Dialogue).Count == 26,
            "Typed pickers did not use the installed SDK kinds.");
        string first = steps.Nodes[0].Key;
        var oldCallbacks = graph.Nodes.Where(node => node.Table?.Field("on_start") is not null)
            .ToDictionary(node => node.Key, node => node.Table!.Field("on_start")!.Value.Read(graph.Source));
        graph = visual.SetReference(first, "directive", MissionReferenceKind.Directive, directive);
        graph = new MissionVisualAuthoring(graph, references).SetReference(first, "navpoint", MissionReferenceKind.Navpoint, navpoint);
        Check(new MissionVisualAuthoring(graph, references).Dialogue(first).Single().Editable,
            "Installed campaign.line aliases were not decoded.");
        graph = new MissionVisualAuthoring(graph, references).SetDialogue(first, 0, cue, null);
        graph = new MissionVisualAuthoring(graph, references).CreateNode(steps.Key, "studio_trigger", MissionNodeTemplate.ReachTrigger,
            trigger, legs.Nodes[0].Key);
        int armCount = graph.Field(legs.Nodes[0].Key, "arm")!.Table!.Fields.Count;
        Check(armCount == 1, "A trigger template duplicated an already armed trigger.");
        Check(graph.Field(steps.Key + "/studio_trigger", "ends.trigger")!.Value.Read(graph.Source) == trigger.Expression,
            "Trigger template wrote a metadata table instead of its exported string alias.");
        foreach (var (key, callback) in oldCallbacks)
            Check(graph.Node(key).Table!.Field("on_start")!.Value.Read(graph.Source) == callback, "Typed editing rewrote an untouched callback.");
        Reject(() => new MissionVisualAuthoring(graph, references).SetReference(first, "ends.ghost_link", MissionReferenceKind.Trigger, trigger));
        Reject(() => new MissionVisualAuthoring(graph, references).CreateNode(steps.Key, "bad_trigger", MissionNodeTemplate.ReachTrigger, trigger));
        Reject(() => graph.AddNode(steps.Key, new string('x', 64)));
        var project = StudioProject.Create(Path.Combine(root, "visual-project"), "Visual mission fixture");
        string file = MissionProject.Save(project, script.RelativePath, graph.Source);
        project = StudioProject.Open(project.DirectoryPath);
        Check(MissionScriptCatalog.ReadSource(file) == graph.Source && project.Changes.Count == 0, "Typed mission did not reopen as a mod script.");
        string export = MissionProject.Export(script.RelativePath, graph.Source, Path.Combine(root, "visual-export"), gameRoot);
        Check(MissionScriptCatalog.ReadSource(script.FullPath) == original, "Visual authoring modified an installed script.");

        string flow = "local missions=require(\"missions\")\nlocal mission=require(missions.MISSION_LIGHTS)\n"
            + "local flow=require(\"lib.flow\")\nreturn flow.new{key=\"studio\",facts={},steps={{id=\"first\"}}}";
        var document = MissionGraphDocument.Parse(flow);
        string stepGroup = document.Groups.Single(value => value.Collection == "steps").Key;
        string factGroup = document.Groups.Single(value => value.Collection == "facts").Key;
        document = new MissionVisualAuthoring(document, references).CreateNode(factGroup, "entered", MissionNodeTemplate.TriggerFact, trigger);
        var sensor = references.Single(value => value.Expression == "mission.Slot.M_DIRECTIVE_SENSOR");
        document = new MissionVisualAuthoring(document, references).CreateNode(stepGroup, "objective", MissionNodeTemplate.FlowObjective,
            directive, sensor: sensor, navpoint: navpoint);
        document = new MissionVisualAuthoring(document, references).CreateNode(stepGroup, "arm", MissionNodeTemplate.FlowArmTrigger, trigger);
        document = new MissionVisualAuthoring(document, references).SetFactCondition(stepGroup + "/objective", "await", "entered");
        document = document.Connect(stepGroup + "/first", stepGroup + "/arm").Connect(stepGroup + "/arm", stepGroup + "/objective");
        string flowPath = Path.Combine(root, "visual-flow.lua"); File.WriteAllText(flowPath, document.Source);
        Console.WriteLine($"mission_visual sdk_aliases=typed triggers=armed callbacks=unchanged project=reopened campaign={export} flow={flowPath}");
        return export;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException or NotSupportedException) { return; }
        throw new InvalidDataException("Invalid typed mission operation was accepted.");
    }
}
