using SunriseStudio.Missions;

/** Scene inputs and outputs, against a small generated SDK module and, when given, an installed one. */
internal static class SceneEventChecks
{
    private const string Module = """
        -- Generated from installed mission data. Do not edit.
        local sdk = require("sunrise.activity_sdk")
        local mission = {
            name = "scene_test",
            id = "scenario/80000001",
            states = {
            S0 = { id = "state/80000001/0000/0000/80000002", ordinal = 0, region_index = 0 },
            },
            slots = {
            SC_A = { id = "slot/a/000000/0000/002b", name = "sc_a", index = 0, type = 43 },
            SC_B = { id = "slot/a/000001/0001/002b", name = "sc_b", index = 1, type = 43 },
            PT_A = { id = "slot/a/000002/0002/001f", name = "pt_a", index = 2, type = 31 },
            },
            squads = {
            },
        }
        mission.Slot = {
            SC_A = "slot/a/000000/0000/002b",
            SC_B = "slot/a/000001/0001/002b",
            PT_A = "slot/a/000002/0002/001f",
        }
        mission.Scene = {
            SC_A = "symbol/80000001/0000/0000/a/28/000000/a/0000/002b",
            SC_B = "symbol/80000001/0000/0000/a/28/000000/a/0001/002b",
        }
        mission.SceneInput = {
            SC_A = {
                T_START = 0x6F51AC66,
                KEY_81BE58A1 = 0x81BE58A1,
            },
            SC_B = {
                T_COMPLETE = 0xB687F07B,
            },
        }
        mission.SceneOutput = {
            SC_A = {
                T_DONE = 0x397A672B,
            },
        }
        return mission
        """;

    private const string Flow = """
        local missions = require("missions")
        local mission = require(missions.SCENE_TEST)
        local flow = require("lib.flow")

        local graph = flow.new{
            key = "scenes",
            facts = {},
            steps = {
                {id = "first"},
            },
        }

        return {
            on_start = function(context, state) graph:advance(context, state) end,
        }
        """;

    private const string Campaign = """
        local missions = require("missions")
        local mission = require(missions.SCENE_TEST)
        local campaign = require("lib.campaign")

        return campaign.new{
            key = "story",
            directive_sensor = mission.Slot.SC_A,
            legs = {{id = "leg", state = mission.states.S0}},
            steps = {{id = "watch"}},
        }
        """;

    /** Runs the checks that use generated fixtures instead of installed game content. */
    public static void Synthetic(string root)
    {
        string game = Path.Combine(root, "scene-game"), sunrise = Path.Combine(game, "bin", "x64", "Sunrise");
        Directory.CreateDirectory(Path.Combine(sunrise, "sdk", "lua", "missions"));
        Directory.CreateDirectory(Path.Combine(sunrise, "scripts", "scene_test"));
        File.WriteAllText(Path.Combine(sunrise, "sdk", "lua", "missions.lua"), "return {\n    SCENE_TEST = \"missions.scene_test\",\n}\n");
        File.WriteAllText(Path.Combine(sunrise, "sdk", "lua", "missions", "scene_test.lua"), Module.ReplaceLineEndings("\n"));
        File.WriteAllText(Path.Combine(sunrise, "scripts", "scene_test", "scene_test.lua"), Flow.ReplaceLineEndings("\n"));
        var catalog = new MissionScriptCatalog(game);
        var refs = catalog.ReadReferences(catalog.Scripts.Single());
        MissionSdkReference Ref(string expression) => refs.Single(value => value.Expression == expression);
        var start = Ref("mission.SceneInput.SC_A.T_START"); var other = Ref("mission.SceneInput.SC_A.KEY_81BE58A1");
        var complete = Ref("mission.SceneInput.SC_B.T_COMPLETE"); var done = Ref("mission.SceneOutput.SC_A.T_DONE");
        var sceneA = Ref("mission.Scene.SC_A"); var slotA = Ref("mission.Slot.SC_A");
        Check(start.Scene == "SC_A" && start.Identity == "0x6F51AC66" && done.Category == "scene_outputs"
            && !refs.Any(value => value.Expression.StartsWith("mission.SceneEvent", StringComparison.Ordinal)),
            "Scene inputs and outputs were not read from the SDK module.");

        var graph = MissionGraphDocument.Parse(Flow.ReplaceLineEndings("\n"));
        var visual = new MissionVisualAuthoring(graph, refs);
        Check(visual.SceneInputs(sceneA).Count == 2 && visual.SceneOutputs(slotA).Single() == done
            && visual.ScenesWithInputs().Count == 2 && visual.ScenesWithOutputs().Single() == slotA,
            "Scene pickers did not group inputs and outputs by scene.");
        string steps = graph.Groups.Single(value => value.Collection == "steps").Key;
        string facts = graph.Groups.Single(value => value.Collection == "facts").Key;
        graph = visual.CreateNode(steps, "send", MissionNodeTemplate.FlowSceneInput, start, scene: sceneA);
        graph = new MissionVisualAuthoring(graph, refs).CreateNode(steps, "begin", MissionNodeTemplate.FlowStartScene,
            scene: sceneA, inputs: [start, other]);
        Check(graph.Source.Contains("run = function(context, state) context:scene(mission.Scene.SC_A):send_event{key = mission.SceneInput.SC_A.T_START} end", StringComparison.Ordinal)
            && graph.Source.Contains("context:scene(mission.Scene.SC_A):activate{} context:scene(mission.Scene.SC_A):send_event{key = mission.SceneInput.SC_A.T_START}"
                + " context:scene(mission.Scene.SC_A):send_event{key = mission.SceneInput.SC_A.KEY_81BE58A1} end", StringComparison.Ordinal),
            "Scene actions did not write direct SDK constants.");
        visual = new MissionVisualAuthoring(graph, refs);
        var begin = visual.ReadFlowAction(steps + "/begin"); var send = visual.ReadFlowAction(steps + "/send");
        Check(begin?.Kind == MissionNodeTemplate.FlowStartScene && begin.Scene == sceneA && begin.Inputs!.SequenceEqual([start, other])
            && send?.Kind == MissionNodeTemplate.FlowSceneInput && send.Target == start && send.Scene == sceneA,
            "Scene actions did not read back as typed actions.");
        Reject(() => visual.CreateNode(steps, "bad", MissionNodeTemplate.FlowSceneInput, complete, scene: sceneA));
        Reject(() => visual.SetFlowAction(steps + "/send", new(MissionNodeTemplate.FlowStartScene, Scene: sceneA, Inputs: [complete])));

        graph = visual.CreateNode(facts, "done", MissionNodeTemplate.SceneOutputFact, done);
        graph = new MissionVisualAuthoring(graph, refs).CreateNode(facts, "entered", MissionNodeTemplate.TriggerFact, Ref("mission.Slot.PT_A"));
        const string binding = "local mission = require(missions.SCENE_TEST)\nlocal EventKind = require(\"sunrise.activity_sdk\").EventKind\n";
        Check(graph.Source.Contains(binding, StringComparison.Ordinal) && graph.Source.Split("local EventKind").Length == 2
            && graph.Source.Contains("return event.kind == EventKind.SCENE_OUTPUT and event.slot ~= nil and event.slot.id == context:slot(mission.Slot.SC_A).id"
                + " and event.key == mission.SceneOutput.SC_A.T_DONE end", StringComparison.Ordinal)
            && graph.Source.Contains("if event.kind == EventKind.PLAYER_TRIGGER and", StringComparison.Ordinal),
            "Fact templates did not compare event.kind with one EventKind binding.");
        Check(Messages(graph, refs).SequenceEqual(["done: waits for a scene output, but the script has no on_event_scene_output callback."]),
            "A scene output fact without its callback was not reported.");
        graph = MissionGraphDocument.Parse(graph.Source.Replace("    on_start = function",
            "    on_event_scene_output = function(context, state, event) graph:handle(context, state, event) end,\n    on_start = function", StringComparison.Ordinal));
        Check(Messages(graph, refs).Count == 0, "A valid scene flow reported a check.");
        graph = new MissionVisualAuthoring(graph, refs).CreateNode(steps, "finish", MissionNodeTemplate.FlowCompleteMission);
        graph = new MissionVisualAuthoring(graph, refs).SetFactCondition(steps + "/finish", "when", "done");
        File.WriteAllText(Path.Combine(root, "scene-flow.lua"), graph.Source);
        var wrong = graph.SetField(steps + "/first", "run", "function(context, state) context:scene(mission.Scene.SC_A):send_event{key = mission.SceneInput.SC_B.T_COMPLETE} end");
        Check(new MissionVisualAuthoring(wrong, refs).ReadFlowAction(steps + "/first") is null
            && Messages(wrong, refs).SequenceEqual(["first: mission.SceneInput.SC_B.T_COMPLETE is not an input of scene SC_A."]),
            "An input sent to another scene was not reported.");
        var missing = graph.SetField(steps + "/first", "run", "function(context, state) context:scene(mission.Scene.SC_A):send_event{key = mission.SceneInput.SC_A.MISSING} end");
        Check(Messages(missing, refs).SequenceEqual(["first: mission.SceneInput.SC_A.MISSING is not in the installed SDK."]),
            "A key missing from the SDK was not reported.");
        var commented = MissionGraphDocument.Parse(graph.Source + "-- context:scene(mission.Scene.SC_A):send_event{key = mission.SceneInput.SC_B.T_COMPLETE}\n");
        Check(Messages(commented, refs).Count == 0, "A commented-out send was checked.");

        var story = MissionGraphDocument.Parse(Campaign.ReplaceLineEndings("\n"));
        string watch = story.Groups.Single(value => value.Collection == "steps").Key + "/watch";
        story = new MissionVisualAuthoring(story, refs).SetStepScene(watch, sceneA);
        story = new MissionVisualAuthoring(story, refs).SetStepInputs(watch, [start, other]);
        story = new MissionVisualAuthoring(story, refs).SetSceneOutput(watch, done);
        visual = new MissionVisualAuthoring(story, refs);
        Check(story.Field(watch, "scene")!.Value.Read(story.Source) == "mission.Scene.SC_A"
            && story.Field(watch, "inputs")!.Value.Read(story.Source) == "lib.list(mission.SceneInput.SC_A.T_START, mission.SceneInput.SC_A.KEY_81BE58A1)"
            && story.Source.Contains("local mission = require(missions.SCENE_TEST)\nlocal lib = require(\"lib.mission_lib\")\n", StringComparison.Ordinal)
            && story.Field(watch, "ends.output")!.Value.Read(story.Source) == "{ slot = mission.Slot.SC_A, key = mission.SceneOutput.SC_A.T_DONE }"
            && visual.StepInputs(watch) is { Editable: true } inputs && inputs.Inputs.SequenceEqual([start, other])
            && visual.StepOutput(watch) == (slotA, done) && Messages(story, refs).Count == 0,
            "Campaign scene fields were not written as direct SDK constants.");
        Reject(() => visual.SetStepInputs(watch, [complete]));
        var moved = visual.SetStepScene(watch, Ref("mission.Scene.SC_B"));
        Check(moved.Field(watch, "inputs") is null && moved.Field(watch, "scene")!.Value.Read(moved.Source) == "mission.Scene.SC_B",
            "A new scene kept inputs of the old scene.");
        var cleared = visual.SetStepScene(watch, null);
        Check(cleared.Field(watch, "scene") is null && cleared.Field(watch, "inputs") is null, "Clearing the scene kept its inputs.");
        Check(Messages(visual.SetReference(watch, "scene", MissionReferenceKind.AuthoredScene, null), refs)
                .SequenceEqual(["watch: has scene inputs but no scene."])
            && Messages(story.SetField(watch, "ends.output", "{ slot = mission.Slot.SC_B, key = mission.SceneOutput.SC_A.T_DONE }"), refs)
                .SequenceEqual(["watch: mission.SceneOutput.SC_A.T_DONE is not an output of scene SC_B."])
            && Messages(story.SetField(watch, "scene", "mission.Slot.SC_A"), refs)
                .SequenceEqual(["watch: scene must be a mission.Scene entry."]),
            "Campaign scene checks missed a defect.");
        Console.WriteLine("mission_scenes inputs=per_scene outputs=per_scene flow_actions=typed facts=event_kind campaign=scene_inputs_output checks=reported"
            + $" game={game} flow={Path.Combine(root, "scene-flow.lua")}");
    }

    /**
     * Reads the installed SDK's scene tables and runs the scene checks over every installed script.
     * @param references SDK references by script name.
     */
    public static void Owned(string game, IReadOnlyDictionary<string, IReadOnlyList<MissionSdkReference>> references)
    {
        var catalog = new MissionScriptCatalog(game);
        int inputs = 0, outputs = 0, scripts = 0;
        foreach (var entry in catalog.Scripts)
        {
            var refs = references[entry.Name];
            inputs += refs.Count(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.SceneInput));
            outputs += refs.Count(value => MissionVisualAuthoring.Accepts(value, MissionReferenceKind.SceneOutput));
            var graph = MissionGraphDocument.Parse(MissionScriptCatalog.ReadSource(entry.FullPath));
            var messages = new MissionVisualAuthoring(graph, refs).SceneDiagnostics();
            if (messages.Count > 0) scripts++;
            foreach (var message in messages) Console.WriteLine($"scene_check {entry.Name}: {message.Message}");
        }
        Check(inputs > 0, "The installed SDK has no scene tables.");
        var vod = catalog.Scripts.Single(value => value.Name == "adventure_vod");
        var sdk = references["adventure_vod"];
        var story = MissionGraphDocument.Parse(MissionScriptCatalog.ReadSource(vod.FullPath));
        string step = story.Nodes.Single(node => node.Kind == "steps" && node.Id == "safety").Key;
        var visual = new MissionVisualAuthoring(story, sdk);
        var scene = visual.Resolve(story.Field(step, "scene")!.Value.Read(story.Source));
        Check(scene?.Expression == "mission.Scene.SCENE_ECHO_INTRO_TWO" && visual.StepInputs(step) is { Editable: true } read
            && read.Inputs.Single().Expression == "mission.SceneInput.SCENE_ECHO_INTRO_TWO.T_ECHO_TWO",
            "Aliased scene and Input constants did not resolve.");
        var edited = visual.SetStepInputs(step, visual.SceneInputs(scene));
        Check(edited.Field(step, "inputs")!.Value.Read(edited.Source).StartsWith("lib.list(mission.SceneInput.SCENE_ECHO_INTRO_TWO.", StringComparison.Ordinal)
            && edited.Source.Split("require(\"lib.mission_lib\")").Length == 2
            && new MissionVisualAuthoring(edited, sdk).SceneDiagnostics().Count == 0,
            "An installed campaign step did not take its scene's inputs.");
        Console.WriteLine($"mission_scenes_installed scripts={catalog.Scripts.Count} inputs={inputs} outputs={outputs} scripts_with_checks={scripts} campaign_edit=pass");
    }

    private static IReadOnlyList<string> Messages(MissionGraphDocument graph, IReadOnlyList<MissionSdkReference> refs) =>
        new MissionVisualAuthoring(graph, refs).SceneDiagnostics().Select(value => value.Message).ToArray();

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException or NotSupportedException) { return; }
        throw new InvalidDataException("Invalid scene edit was accepted.");
    }
}
