// Mission insight checks: the SDK mission module reader, the coverage report (mission_coverage), the per-slot facts
// list_mission_objects and get_mission_object add (squads with no data, cutscene state from tag bytes 0x0C-0x0F, dialogue
// text, trigger map), and the retail timeline reader and its saved marks. No game install needed.
using System.Text.Json.Nodes;
using Solforge.Mcp;

static class MissionInsightChecks
{
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("mission insight: " + message);
    }

    /** A cut-down module in the generator's layout (mission_calculon, 'Six'). */
    public const string Module = """
        -- Generated from installed mission data. Do not edit.
        local sdk = require("sunrise.activity_sdk")

        ---@type SunriseMission
        local mission = {
            name = "mission_fixture",
            id = "scenario/80b42baa",
            tag = 0x80B42BAA,
            states = {
            STATE_80B42BAA_0000_0000_80B42B76 = { id = "state/80b42baa/0000/0000/80b42b76", ordinal = 0, slice_set_index = 0, region_index = 0, hash = 0xAC2C2BB9, value = 0 },
            STATE_80B42BAA_0001_0000_80B42B77 = { id = "state/80b42baa/0001/0000/80b42b77", ordinal = 0, slice_set_index = 8, region_index = 8, hash = 0x54BD10BD, value = 1 },
            },
            slots = {
            SQ_BOUNCER_FRONT_RIGHT = { id = "slot/80b42ecf/000015/0015/0001", name = "sq_bouncer_front_right", index = 21, type = 1, auth_type = "squad_sensor" },
            SQ_BOUNCER_FRONT_LEFT = { id = "slot/80b42ecf/000016/0016/0001", name = "sq_bouncer_front_left", index = 22, type = 1, auth_type = "squad_sensor" },
            SQ_FODDER = { id = "slot/80b42ecf/000017/0017/0001", name = "sq_fodder", index = 23, type = 1, auth_type = "squad_sensor" },
            SQ_OTHER_PREFAB = { id = "slot/80c02b09/000001/0001/0001", name = "sq_other", index = 1, type = 1, auth_type = "squad_sensor" },
            OUTRO_CINEMATIC_CINEMATIC = { id = "slot/80b42ee7/000000/0000/0006", name = "outro_cinematic._cinematic", index = 0, type = 6, auth_type = "cinematic_sensor" },
            D_VEX_ALARM_1 = { id = "slot/80b42ecf/000060/0060/0017", name = "d_vex_alarm_1", index = 96, type = 23, auth_type = "device_sensor" },
            M_DIALOG_SENSOR_80B43319 = { id = "slot/80b43319/000000/0000/0035", name = "m_dialog_sensor", index = 0, type = 53, auth_type = "dialog_sensor" },
            TV_BOUNCER_FRONT_LEFT = { id = "slot/80b42ecf/000099/0099/003c", name = "tv_bouncer_front_left", index = 153, type = 60 },
            },
            squads = {
            SQ_BOUNCER_FRONT_RIGHT = { id = "squad/5e3b", slot = 8044, spawner_config = 0x80B43645, members = {
                    { actor_class = 0x80BFB34C, behavior_config = 0x80C23F66, default_faction = 0 },
                }, anchors = {
                    { x = 1, y = 2, z = 3, point = 0 },
                } },
            SQ_FODDER = { id = "squad/aaaa", slot = 8045, members = {
                }, anchors = {
                } },
            },
            scenes = {
            SCENE_COLONY_CAYDE = { id = "symbol/80b42baa/0001/0000/80b42bad/18/000022/80c03459/003f/002b", slot = 86272, config_tag = 0x80C03355, resource_tag = 0x80C0090D, event_keys = { 0x385838EC } },
            SC_EXPLOSION_BACKUP_0 = { id = "symbol/80b42baa/0002/0000/80b42baf/18/000005/80c038b7/00df/002b", slot = 87251, config_tag = 0x80C03893, resource_tag = 0x80C005AF, event_keys = { 0x385838EC } },
            },
        }

        mission.DialogueCue = {
            M_DIALOG_SENSOR_80B43319 = {
                CUE_0 = 0,
                CUE_1 = 1,
                CUE_2 = 2,
            },
        }
        mission.DialogueCueVariants = {
            M_DIALOG_SENSOR_80B43319 = {
                [0] = {
                    "OK. So we find this Vex teleporter and shut it down. Easy.",
                    "It's me! It's the \"Cayde Unit\"? {braces}",
                },
                [1] = {
                    "Yecch! What is this stuff?",
                },
                [2] = {},
            },
        }
        mission.Directive = {
            RENDEZVOUS_WITH_CAYDE_6 = { id = "directive/00002207/0a8f5772/0", slot_row = 8711, sensor = "slot/80b43319/000000/0000/0044", name_hash = 0x0A8F5772, element = 0, title = "Rendezvous with Cayde-6", description = "Make your way toward the Exodus Black." },
            FREE_CAYDE_6 = { id = "directive/00002207/0cc5e7fc/0", slot_row = 8711, sensor = "slot/80b43319/000000/0000/0044", name_hash = 0x0CC5E7FC, element = 0, title = "Free Cayde-6", description = "Find the Vex Teleporter." },
        }
        mission.trigger_volumes = {
            { names = {"pt_bouncer_front_left", }, registry_key = 0x80B42ECF, slot_type = 60, slot_index = 153, position = { x = 0, y = 0, z = 0 }, minimum = { x = 0, y = 0, z = 0 }, maximum = { x = 10, y = 10, z = 10 }, shape_tag = 0x80B43475, shape_index = 0, active = 1 },
            { names = {}, registry_key = 0x80B42ECF, slot_type = 60, slot_index = 154, position = { x = 0, y = 0, z = 0 }, minimum = { x = 5, y = 5, z = 5 }, maximum = { x = 20, y = 20, z = 20 }, shape_tag = 0x80B43475, shape_index = 1, active = 1 },
            { names = {"far_away", }, registry_key = 0x80B42ECF, slot_type = 60, slot_index = 155, position = { x = 0, y = 0, z = 0 }, minimum = { x = 100, y = 100, z = 100 }, maximum = { x = 110, y = 110, z = 110 }, shape_tag = 0x80B43475, shape_index = 2, active = 1 },
        }
        mission.TriggerVolume = {
            PT_BOUNCER_FRONT_LEFT = mission.trigger_volumes[1],
        }
        return mission
        """;

    public const string Script = """
        -- Six. Red War campaign draft; not tested in game.
        local missions = require("missions")
        local mission = require(missions.MISSION_FIXTURE)
        local Slot, Squad, Directive = mission.Slot, mission.Squad, mission.Directive
        local cue = mission.DialogueCue.M_DIALOG_SENSOR_80B43319
        -- SQ_FODDER and cue.CUE_1 only in a comment, so not used.
        local stairs = {
            id = "stairs",
            squads = { unit(Squad.SQ_BOUNCER_FRONT_RIGHT, Slot.SQ_BOUNCER_FRONT_RIGHT) },
            lines = { line(cue.CUE_0) },
        }
        local last = { id = "last", directive = Directive.RENDEZVOUS_WITH_CAYDE_6, scenes = { scene(mission.Scene.SCENE_COLONY_CAYDE) } }
        --[[ id = "commented_out" ]]
        """;

    public const string Timeline = """
        # Six retail timeline (5 fps capture)

        Source: a video.

        | Video time | Beat |
        |---|---|
        | 0:00-0:02 | IGN "Spoiler" card |
        | 0:45-1:40 | Vex fight among Vex gates and pillars, sniper |
        | 16:12-18:17 | Cutscene in a dark ship interior: Cayde-6 | dialogue |

        Trailing text.
        """;

    /** Entry point: runs every check in this class and throws on the first failure. */
    public static async Task Run(string scratch, FixtureHost host)
    {
        Directory.CreateDirectory(scratch);
        var sdk = MissionSdkData.Parse(Module, "fixture.lua");
        Expect(sdk.Name == "mission_fixture" && sdk.Tag == 0x80B42BAA, "name and tag");
        Expect(sdk.Slots.Count == 8 && sdk.SlotById("slot/80b42ecf/000016/0016/0001")?.Key == "SQ_BOUNCER_FRONT_LEFT", "slots");
        Expect(sdk.Squads.Count == 2 && sdk.Squads["SQ_BOUNCER_FRONT_RIGHT"] == 1 && sdk.Squads["SQ_FODDER"] == 0, "squad members");
        Expect(sdk.SquadHasData(sdk.SlotByKey("SQ_BOUNCER_FRONT_RIGHT")!) && !sdk.SquadHasData(sdk.SlotByKey("SQ_BOUNCER_FRONT_LEFT")!)
            && !sdk.SquadHasData(sdk.SlotByKey("SQ_FODDER")!), "squads with no data (missing row, no members)");
        Expect(sdk.States.Count == 2 && sdk.States[1].Tag == 0x80B42B77 && sdk.States[1].Index == 1 && sdk.States[1].Hash == 0x54BD10BD, "states");
        Expect(sdk.StateFor(0x80B42B77)?.Key == "STATE_80B42BAA_0001_0000_80B42B77" && sdk.StateFor(0x54BD10BD) is not null
            && sdk.StateFor(1)?.Index == 1 && sdk.StateFor(0x12345678) is null, "state lookup by tag, hash and index");
        Expect(sdk.Directives.Count == 2 && sdk.Directives[0].Title == "Rendezvous with Cayde-6", "directives");
        Expect(sdk.DialogueCues["M_DIALOG_SENSOR_80B43319"].Count == 3, "dialogue cues");
        Expect(sdk.Lines("M_DIALOG_SENSOR_80B43319", 0).Count == 2 && sdk.Lines("M_DIALOG_SENSOR_80B43319", 0)[1] == "It's me! It's the \"Cayde Unit\"? {braces}"
            && sdk.Lines("M_DIALOG_SENSOR_80B43319", 2).Count == 0 && sdk.HasDialogueText, "dialogue text, escapes and braces in strings");
        Expect(sdk.Scenes.Count == 2 && sdk.Scenes[0].ResourceTag == 0x80C0090D, "scenes");
        Expect(sdk.TriggerVolumes.Count == 3 && sdk.TriggerVolumes[0].Label == "pt_bouncer_front_left" && sdk.TriggerVolumes[1].Label == "trigger_volumes[2]"
            && sdk.TriggerVolumes[1].Minimum[0] == 5 && sdk.TriggerVolumes[1].Maximum[2] == 20, "trigger volumes");
        Expect(sdk.VolumeFor(0x80B42ECF, 60, 153, "tv_bouncer_front_left")?.Number == 1
            && sdk.VolumeFor(0x80B42ECF, 31, 78, "pt_bouncer_front_left")?.Number == 1, "volume by slot and by name");
        Expect(!MissionSdkData.Parse(Module.Replace("\"OK. So we find this Vex teleporter and shut it down. Easy.\",", "")
            .Replace("\"It's me! It's the \\\"Cayde Unit\\\"? {braces}\",", "").Replace("\"Yecch! What is this stuff?\",", "")).HasDialogueText,
            "a mission with no dialogue text");

        // Coverage.
        var script = new MissionScriptFile("mission_fixture.lua", Script, MissionInsight.RetailName(Script));
        Expect(script.RetailName == "Six" && MissionInsight.RetailName("-- 1AU. Red War") == "1AU" && MissionInsight.RetailName("local x = 1") is null, "retail name");
        var report = MissionInsight.Coverage(sdk, script);
        CoverageCategory Category(MissionCoverageReport r, string name) => r.Categories.First(c => c.Name == name);
        var squads = Category(report, "squads");
        Expect(squads.Total == 3 && squads.Used == 1 && squads.OutOfScope == 1
            && squads.Unused.Select(item => item.Key).SequenceEqual(["SQ_BOUNCER_FRONT_LEFT", "SQ_FODDER"])
            && squads.Unused.All(item => item.Detail == "no squad data; cannot be placed"), "squad coverage, scope and no-data marks");
        var all = MissionInsight.Coverage(sdk, script, all: true);
        Expect(Category(all, "squads").Total == 4 && Category(all, "squads").OutOfScope == 0, "all=true counts every prefab");
        var directives = Category(report, "directives");
        Expect(directives.Used == 1 && directives.Unused.Single().Key == "FREE_CAYDE_6", "directive coverage");
        var dialogue = Category(report, "dialogue");
        Expect(dialogue.Total == 3 && dialogue.Used == 1 && dialogue.Unused[0].Key == "M_DIALOG_SENSOR_80B43319.CUE_1"
            && dialogue.Unused[0].Label == "Yecch! What is this stuff?" && dialogue.Unused[1].Label == "(no text)", "dialogue coverage via the cue alias; comments ignored");
        Expect(Category(report, "cutscenes").Unused.Single().Key == "OUTRO_CINEMATIC_CINEMATIC", "cutscene coverage");
        Expect(Category(report, "interactables").Unused.Single().Key == "D_VEX_ALARM_1", "interactable coverage");
        Expect(Category(report, "scenes").Unused.Single().Key == "SC_EXPLOSION_BACKUP_0", "scene coverage");
        var json = report.ToJson(1);
        Expect(json["retailName"]?.ToString() == "Six" && json["categories"]?["squads"]?["truncated"]?.GetValue<bool>() == true
            && json["categories"]?["squads"]?["unused"]?.AsArray().Count == 1, "report JSON and limit");
        var none = MissionInsight.Coverage(sdk, null);
        Expect(none.ScriptPath is null && Category(none, "directives").Used == 0 && none.ToJson(10)["note"] is not null, "no script: everything unused");

        // Cutscene state: u32 at 0x0C of the first candidate tag that names a state.
        byte[] Bytes(uint value) { var bytes = new byte[32]; BitConverter.GetBytes(value).CopyTo(bytes, MissionInsight.CutsceneStateOffset); return bytes; }
        var tags = new Dictionary<uint, byte[]> { [0x80B42EE7] = Bytes(0x80B42B77), [0x80AAAAAA] = Bytes(0xDEADBEEF) };
        var state = MissionInsight.CutsceneState(sdk, [0, 0x80AAAAAA, 0x80B42EE7], tag => tags.GetValueOrDefault(tag));
        Expect(state?["state"]?.ToString() == "STATE_80B42BAA_0001_0000_80B42B77" && state?["fromTag"]?.ToString() == "0x80B42EE7"
            && state?["index"]?.GetValue<int>() == 1, "cutscene state decoded from bytes 0x0C-0x0F");
        var raw = MissionInsight.CutsceneState(sdk, [0x80AAAAAA], tag => tags.GetValueOrDefault(tag));
        Expect(raw?["bytes0C"]?.ToString() == "0xDEADBEEF" && raw?["state"] is null && raw?["note"] is not null, "unknown state stays raw");
        Expect(MissionInsight.CutsceneState(sdk, [0x80000001], _ => new byte[8]) is null, "short tag is skipped");

        // Per-slot facts.
        MissionSlot Slot(string id, string name, int type, float[]? position = null)
        {
            MissionSlots.TryParseSlotId(id, out uint registry, out int slotType, out int index);
            return new(id, name, registry, slotType == 0 ? type : slotType, index, 0) { Position = position };
        }
        var snapshot = new MissionSnapshot("Nessus", 0x80B42BAA, "mission_fixture",
        [
            Slot("slot/80b42ecf/000016/0016/0001", "sq_bouncer_front_left", 1),
            Slot("slot/80b42ee7/000000/0000/0006", "outro_cinematic._cinematic", 6),
            Slot("slot/80b43319/000000/0000/0035", "m_dialog_sensor", 53),
            Slot("slot/80b42ecf/000099/0099/003c", "tv_bouncer_front_left", 60),
            Slot("slot/80b42ecf/0000aa/00aa/0032", "player_spawn_point", 50, [1, 1, 1]),
        ]);
        var spawns = MissionInsight.Spawns(snapshot);
        Expect(spawns.Count == 1 && spawns[0].Name == "player_spawn_point", "spawn points");
        var row = new JsonObject();
        MissionInsight.Annotate(row, snapshot.Find("sq_bouncer_front_left")!, sdk, false, spawns, null);
        Expect(row["squadData"]?.GetValue<bool>() == false && row["squadNote"] is not null, "list row: squad with no data");
        row = new JsonObject();
        MissionInsight.Annotate(row, snapshot.Find("outro_cinematic._cinematic")!, sdk, true, spawns, tag => tags.GetValueOrDefault(tag));
        Expect(row["cutsceneState"]?["state"]?.ToString() == "STATE_80B42BAA_0001_0000_80B42B77", "details: cutscene state from the registry tag");
        row = new JsonObject();
        MissionInsight.Annotate(row, snapshot.Find("m_dialog_sensor")!, sdk, false, spawns, null);
        Expect(row["dialogueCues"]?.GetValue<int>() == 3 && row["dialogueFirst"]?.ToString()?.StartsWith("OK. So") == true, "list row: dialogue inline");
        row = new JsonObject();
        MissionInsight.Annotate(row, snapshot.Find("m_dialog_sensor")!, sdk, true, spawns, null);
        Expect(row["dialogue"]?.AsArray().Count == 3 && row["dialogue"]?[0]?["text"]?.AsArray().Count == 2, "details: every line's text");
        row = new JsonObject();
        MissionInsight.Annotate(row, snapshot.Find("tv_bouncer_front_left")!, sdk, true, spawns, null);
        Expect(row["triggerMap"]?["containsSpawn"]?[0]?.ToString() == "player_spawn_point"
            && row["triggerMap"]?["overlaps"]?[0]?.ToString() == "trigger_volumes[2]" && row["triggerBounds"] is not null, "details: trigger map");
        var map = MissionInsight.TriggerMap(sdk, spawns);
        Expect(map.Count == 2 && map.All(entry => entry?["volume"]?.ToString() != "far_away"), "trigger map skips lone volumes");

        // Files: SDK lookup by tag and name, script lookup, and the mission_coverage tool by retail name.
        string sdkFolder = Path.Combine(scratch, "sdk", "missions"), scripts = Path.Combine(scratch, "project", "scripts");
        Directory.CreateDirectory(sdkFolder);
        Directory.CreateDirectory(Path.Combine(scripts, "mission_fixture"));
        File.WriteAllText(Path.Combine(sdkFolder, "mission_fixture_80b42baa.lua"), Module);
        File.WriteAllText(Path.Combine(scripts, "mission_fixture", "mission_fixture.lua"), Script);
        Expect(MissionSdkData.ForTag([sdkFolder], 0x80B42BAA)?.Name == "mission_fixture" && MissionSdkData.ForName([sdkFolder], "mission_fixture") is not null
            && MissionSdkData.ForName([sdkFolder], "mission") is null, "module lookup");
        Expect(MissionInsight.FindScript([scripts], "mission_fixture")?.RetailName == "Six", "script lookup");
        var tool = SolforgeTools.Create(host).Single(each => each.Name == "mission_coverage");
        var savedRoots = host.CodeRoots;
        host.CodeRoots = [new CodeRoot("sdk-missions", sdkFolder, true), new CodeRoot("server-scripts", scripts)];
        try
        {
            var result = await tool.Handler(new JsonObject { ["mission"] = "six" }, CancellationToken.None);
            var body = JsonNode.Parse(result.Texts[0])!;
            Expect(!result.IsError && body["mission"]?.ToString() == "mission_fixture" && body["categories"]?["directives"]?["unusedCount"]?.GetValue<int>() == 1,
                "mission_coverage by retail name: " + result.Texts[0]);
            var byTag = await tool.Handler(new JsonObject { ["mission"] = "0x80B42BAA" }, CancellationToken.None);
            Expect(!byTag.IsError && JsonNode.Parse(byTag.Texts[0])!["script"] is not null, "mission_coverage by tag finds the script");
            var missing = await tool.Handler(new JsonObject { ["mission"] = "nothing_here" }, CancellationToken.None);
            Expect(missing.IsError, "unknown mission is an error");
        }
        finally { host.CodeRoots = savedRoots; }

        // Retail timeline and saved marks.
        string solforge = Path.Combine(scratch, "project", ".solforge"), reference = Path.Combine(solforge, "reference");
        Directory.CreateDirectory(reference);
        File.WriteAllText(Path.Combine(reference, "six" + RetailTimeline.Suffix), Timeline);
        File.WriteAllText(Path.Combine(reference, "1au" + RetailTimeline.Suffix), "| Time | Beat |\n|--|--|\n| 0:01 | x |\n");
        var timeline = RetailTimeline.Load(Path.Combine(reference, "six" + RetailTimeline.Suffix));
        Expect(timeline.Title.StartsWith("Six retail timeline") && timeline.Beats.Count == 3 && timeline.Beats[1].Time == "0:45-1:40"
            && timeline.Beats[2].Beat == "Cutscene in a dark ship interior: Cayde-6 | dialogue" && timeline.Key == "six", "timeline table");
        Expect(RetailTimeline.ReferenceFolder(Path.Combine(scratch, "project"), null) == reference
            && RetailTimeline.ReferenceFolder(null, Path.Combine(scripts, "mission_fixture", "mission_fixture.lua")) == reference, "reference folder");
        Expect(RetailTimeline.Find(reference, "Six")?.EndsWith("six" + RetailTimeline.Suffix) == true && RetailTimeline.Find(reference, "1AU") is not null
            && RetailTimeline.Find(reference, "Hope") is null && RetailTimeline.Available(reference).Count == 2, "timeline by retail name");
        Expect(RetailTimeline.Steps(Script).SequenceEqual(["stairs", "last"]), "steps from the script, comments skipped");
        var marks = RetailCoverage.Open(solforge);
        Expect(!marks.Mark("mission_fixture", "0:45-1:40").Covered && marks.Timeline("mission_fixture") is null, "empty marks");
        marks.SetMark("mission_fixture", "0:45-1:40", new RetailMark(true, "stairs"));
        marks.SetMark("mission_fixture", "0:00-0:02", new RetailMark(true, null));
        marks.SetTimeline("mission_fixture", "six");
        marks.Save();
        var reopened = RetailCoverage.Open(solforge);
        Expect(reopened.Mark("mission_fixture", "0:45-1:40") == new RetailMark(true, "stairs") && reopened.Timeline("mission_fixture") == "six"
            && reopened.CoveredCount("mission_fixture", timeline) == 2 && File.Exists(Path.Combine(solforge, RetailCoverage.FileName)), "marks saved in the project");
        reopened.SetMark("mission_fixture", "0:00-0:02", new RetailMark(false, null));
        reopened.Save();
        Expect(RetailCoverage.Open(solforge).CoveredCount("mission_fixture", timeline) == 1, "unchecking removes the mark");
        File.WriteAllText(Path.Combine(solforge, RetailCoverage.FileName), "{ broken");
        Expect(RetailCoverage.Open(solforge).CoveredCount("mission_fixture", timeline) == 0, "a damaged marks file reads as empty");
    }
}
