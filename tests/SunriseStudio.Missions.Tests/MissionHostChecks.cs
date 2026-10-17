using SunriseStudio.Missions;

/** Check script and Simulate (MissionHost): syntax, keys, hashes, library checks, step walks, stalls and mocks. */
internal static class MissionHostChecks
{
    private const string Tag = "0x80000001";
    private const string SlotA = "slot/80000001/000000/0000/0001";
    private const string SlotB = "slot/80000001/000001/0001/0001";

    /** A fake SDK, a tiny campaign library and scripts written to the temporary folder. */
    public static void Synthetic(string temporary)
    {
        string sdk = Path.Combine(temporary, "host-sdk"), scripts = Path.Combine(temporary, "host-scripts");
        Write(Path.Combine(sdk, "missions.lua"), "return { MISSION_FAKE = \"missions.fake_80000001\" }\n");
        Write(Path.Combine(sdk, "missions", "fake_80000001.lua"),
            $"local mission = {{ tag = {Tag} }}\nmission.Slot = {{ A = \"{SlotA}\", B = \"{SlotB}\" }}\nreturn mission\n");
        Write(Path.Combine(scripts, "lib", "campaign.lua"), Campaign);

        // A clean campaign walks both steps and completes once in every mode; the unknown context call is a mock.
        string good = Script(scripts, "mission_good", "trigger = Slot.B", "context:spawn_fireworks()");
        var check = MissionHost.Check(good, sdk);
        Check(check.Passed && check.MissionTag == 0x80000001u, "A clean script failed Check script: " + Dump(check));
        foreach (string mode in MissionHost.Modes)
        {
            var run = MissionHost.Simulate(good, sdk, mode);
            Check(run.Passed && run.Records.Any(record => record.Text == "WALKED ALL STEPS")
                && run.Records.Any(record => record.Text.StartsWith("COMPLETES 1 ", StringComparison.Ordinal)), $"{mode} did not walk: " + Dump(run));
            Check(run.Trace.Count(record => record.Kind == "step") == 2 && run.Trace.First().Line == Line(good, "\"one\""),
                $"{mode} trace has no step lines: " + Dump(run));
            Check(run.Mocks.Any(record => record.Text.StartsWith("context:spawn_fireworks", StringComparison.Ordinal)
                && record.Line == Line(good, "spawn_fireworks")), $"{mode} missed the missing mock: " + Dump(run));
        }
        Check(MissionHost.Simulate(good, sdk, "rejoin").Trace.Any(record => record.Text.StartsWith("REJOIN", StringComparison.Ordinal)),
            "rejoin did not rejoin");
        Check(MissionHost.NormalizeMode("guard") == "cutscene_never_loads" && MissionHost.NormalizeMode("reload") == "rejoin"
            && MissionHost.NormalizeMode("cutscene never loads") == "cutscene_never_loads" && MissionHost.NormalizeMode("dance") is null,
            "mode names");

        // The open mission's objects (list_mission_objects) must know each Slot constant when the tags match.
        var objects = MissionHost.Check(good, sdk, 0x80000001u, key => key == "A");
        Check(objects.Problems.Any(record => record.Kind == "object" && record.Text.StartsWith("Slot.B ", StringComparison.Ordinal)
            && record.Line == Line(good, "Slot.B")), "An object missing from the open mission was not reported: " + Dump(objects));
        Check(MissionHost.Check(good, sdk, 0x80000002u, _ => false).Passed, "Another mission's objects were used.");

        // Syntax: Loretta reports the line, nothing runs.
        string broken = Script(scripts, "mission_syntax", "trigger = Slot.B", "local x = = 1");
        var syntax = MissionHost.Check(broken, sdk);
        Check(syntax.Problems.Any(record => record.Kind == "syntax" && record.Line == Line(broken, "local x = = 1")), "syntax: " + Dump(syntax));

        // A missing key, a hash the game data lacks and the library's check(): each on its own line.
        string bad = Script(scripts, "mission_bad", "trigger = Slot.NOPE", "local tag = 0x12345678");
        var problems = MissionHost.Check(bad, sdk);
        Check(problems.Problems.Any(record => record.Kind == "key" && record.Text.StartsWith("mission.Slot.NOPE ", StringComparison.Ordinal)
            && record.Line == Line(bad, "Slot.NOPE")), "missing key: " + Dump(problems));
        Check(problems.Problems.Any(record => record.Kind == "hash" && record.Line == Line(bad, "0x12345678")), "bad hash: " + Dump(problems));
        Check(problems.Problems.Any(record => record.Kind == "check" && record.Text.Contains("two ends on nothing", StringComparison.Ordinal)
            && record.Line == Line(bad, "\"two\"")), "library check: " + Dump(problems));

        // A step whose end the library never reports is a stall at that step's line.
        string stalls = Script(scripts, "mission_stall", "trigger = Slot.B, interact_only = true", "");
        var stall = MissionHost.Simulate(stalls, sdk, "play");
        Check(stall.Stalls.Any(record => record.Text.StartsWith("stalled at step two", StringComparison.Ordinal) && record.Line == Line(stalls, "\"two\"")),
            "stall: " + Dump(stall));

        // A script error stops the walk and names the script line.
        string throws = Script(scripts, "mission_throws", "trigger = Slot.B", "error(\"boom\")");
        var thrown = MissionHost.Simulate(throws, sdk, "play");
        Check(thrown.Problems.Any(record => record.Kind == "error" && record.Text.Contains("boom", StringComparison.Ordinal)
            && record.Line == Line(throws, "boom")), "script error: " + Dump(thrown));
    }

    /**
     * The Red War campaign (Project Sol): every mission_* script passes Check script and walks all steps once in every
     * mode. scripts is the project's scripts folder; game is the installation whose SDK the missions require.
     */
    public static void RedWar(string scripts, string game)
    {
        string sdk = MissionHost.SdkRootOf(game);
        int count = 0;
        foreach (string folder in Directory.EnumerateDirectories(scripts, "mission_*").Order(StringComparer.Ordinal))
        {
            string path = Path.Combine(folder, Path.GetFileName(folder) + ".lua");
            if (!File.Exists(path)) continue;
            var check = MissionHost.Check(path, sdk);
            Check(check.Passed, $"{Path.GetFileName(folder)} Check script: " + Dump(check));
            foreach (string mode in MissionHost.Modes)
            {
                var run = MissionHost.Simulate(path, sdk, mode);
                Check(run.Passed && run.Records.Any(record => record.Text == "WALKED ALL STEPS")
                    && run.Records.Any(record => record.Text.StartsWith("COMPLETES 1 ", StringComparison.Ordinal)),
                    $"{Path.GetFileName(folder)} {mode}: " + Dump(run));
            }
            count++;
        }
        Check(count > 0, "No mission_* scripts under " + scripts);
        Console.WriteLine($"mission_host_red_war {count} scripts x {MissionHost.Modes.Length} modes");
    }

    // A step ends on its trigger; the library logs "step <id> done" and completes after the last step.
    private const string Campaign = """
        local campaign = {}
        local function check(content)
            assert(content.key ~= nil, "missing mission constant: campaign key")
            for _, step in ipairs(content.steps) do
                assert(step.ends ~= nil and step.ends.trigger ~= nil, step.id .. " ends on nothing")
            end
        end
        function campaign.new(content)
            check(content)
            local index = 1
            return {
                on_start = function(context, state) if content.on_start then content.on_start(context) end end,
                on_load = function(context, state) end,
                on_event_client_state_changed = function(context, state, event) end,
                on_event_player_trigger = function(context, state, event)
                    local step = content.steps[index]
                    if step ~= nil and not step.ends.interact_only and event.slot.id == step.ends.trigger then
                        context:log("step " .. step.id .. " done")
                        index = index + 1
                        if index > #content.steps then context:complete_mission() end
                    end
                end,
            }
        end
        return campaign
        """;

    /** scripts/name/name.lua: two steps, the second's ends table holding secondEnds; extra goes in on_start. */
    private static string Script(string scripts, string name, string secondEnds, string extra)
    {
        string path = Path.Combine(scripts, name, name + ".lua");
        Write(path, string.Join('\n',
            "local missions = require(\"missions\")",
            "local mission = require(missions.MISSION_FAKE)",
            "local campaign = require(\"lib.campaign\")",
            "local Slot = mission.Slot",
            "return campaign.new{",
            "    key = \"fake\",",
            "    legs = { { id = \"leg\", state = { region_index = 0 } } },",
            "    steps = {",
            "        { id = \"one\", ends = { trigger = Slot.A } },",
            "        { id = \"two\", ends = { " + secondEnds + " } },",
            "    },",
            "    on_start = function(context)",
            "        " + extra,
            "    end,",
            "}",
            ""));
        return path;
    }

    private static int Line(string path, string text) =>
        Array.FindIndex(File.ReadAllLines(path), line => line.Contains(text, StringComparison.Ordinal)) + 1;

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n"));
    }

    private static string Dump(MissionHostReport report) => string.Join(" | ", report.Records.Where(record => record.Kind != "trace")
        .Select(record => $"{record.Kind}@{record.Line} {record.Text}"));

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
