using System.Text;
using System.Text.RegularExpressions;

namespace SunriseStudio.Missions;

/** The kinds of activity a new mission script can start as; each has a starter template. */
public enum MissionActivityType { Story, Strike, Patrol, PublicEvent, LostSector, Raid, Encounter, Pvp, Quest, Custom, CampaignCinematic, StrikeBoss, RaidEncounter }

/** One activity type's picker row: its MCP id, label and what the template builds. */
public sealed record MissionTemplateInfo(MissionActivityType Type, string Id, string Label, string Description);

/** A finding from the template checks: a placeholder left to fill, or an SDK name the mission does not have. */
public sealed record MissionTemplateFinding(string Message, int Line);

/**
 * Starter server Lua for each activity type, built on the installed libs: lib.campaign (story, strike, raid,
 * encounter, quest), lib.freeroam (patrol, public event, lost sector), lib.mission_lib (pvp-style modes and the blank
 * custom script). Names come from the chosen mission's SDK rows (Slot, Squad, Directive, states); a name the SDK
 * does not have becomes a CHANGE_ME placeholder that Checks lists until it is replaced.
 */
public static class MissionTemplates
{
    public const string Placeholder = "CHANGE_ME";

    public static IReadOnlyList<MissionTemplateInfo> Types { get; } =
    [
        new(MissionActivityType.Story, "story", "Story / campaign mission", "lib.campaign: a leg, directive steps that clear encounters, a boss step that completes the mission."),
        new(MissionActivityType.Strike, "strike", "Strike", "lib.campaign: clear-driven steps through the map and a boss barrier step; complete pays XP and a roll."),
        new(MissionActivityType.Patrol, "patrol", "Patrol / freeroam", "lib.freeroam: one zone with patrol squads that respawn."),
        new(MissionActivityType.PublicEvent, "public_event", "Public event", "lib.freeroam: a zone with an event site (sensor, objective, squads, chest)."),
        new(MissionActivityType.LostSector, "lost_sector", "Lost sector", "lib.freeroam: a zone with a sector (squads, boss, chest)."),
        new(MissionActivityType.Raid, "raid", "Raid / encounter chain", "lib.campaign: one barrier step per encounter, each cleared to move on."),
        new(MissionActivityType.Encounter, "encounter", "Single encounter", "lib.campaign: one step that places one encounter and completes on its clear."),
        new(MissionActivityType.Pvp, "pvp", "Crucible / Gambit-style mode", "lib.mission_lib: a timed round with a score variable; ends the round on the timer."),
        new(MissionActivityType.Quest, "quest", "Quest", "lib.campaign: ordered objective steps with a counter step."),
        new(MissionActivityType.Custom, "custom", "Blank custom script", "The bare runtime table: initial state and the start and load callbacks."),
        new(MissionActivityType.CampaignCinematic, "campaign_cinematic", "Campaign mission with cutscenes and transitions",
            "lib.campaign + lib.mission_lib: an opening cutscene, a fade/teleport area transition between steps, a boss step and an ordered ending."),
        new(MissionActivityType.StrikeBoss, "strike_boss", "Strike with a boss phase",
            "lib.campaign + lib.mission_lib: clear-driven steps, then a boss barrier step whose shield and pylon phase starts at half health; ordered ending."),
        new(MissionActivityType.RaidEncounter, "raid_encounter", "Raid encounter skeleton",
            "lib.campaign + lib.mission_lib: a mechanic step (boss invulnerable), a damage phase with pylons, a final stand with a transition out, and an ordered ending."),
    ];

    /** Parses an MCP activity id or label (story, strike, patrol, public_event, lost_sector, raid, encounter, pvp, quest, custom). */
    public static MissionActivityType Parse(string? text)
    {
        string value = (text ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        foreach (var info in Types) if (info.Id == value) return info.Type;
        return value switch
        {
            "campaign" or "mission" => MissionActivityType.Story,
            "freeroam" or "destination" => MissionActivityType.Patrol,
            "event" or "publicevent" => MissionActivityType.PublicEvent,
            "lostsector" or "sector" => MissionActivityType.LostSector,
            "crucible" or "gambit" or "mode" => MissionActivityType.Pvp,
            "dungeon" => MissionActivityType.Raid,
            "cinematic" or "campaign_cutscene" => MissionActivityType.CampaignCinematic,
            "boss_strike" => MissionActivityType.StrikeBoss,
            "raid_boss" or "encounter_skeleton" => MissionActivityType.RaidEncounter,
            "" or "blank" => MissionActivityType.Custom,
            _ => throw new InvalidDataException("Unknown activity type " + text + "; use one of " + string.Join(", ", Types.Select(info => info.Id)) + ".")
        };
    }

    /** True for the types built on lib.freeroam (no campaign node graph; edited as Lua with Checks). */
    public static bool IsFreeroam(MissionActivityType type) =>
        type is MissionActivityType.Patrol or MissionActivityType.PublicEvent or MissionActivityType.LostSector;

    /**
     * Builds the script.
     * @param missionKey The missions.lua key (e.g. STRIKE_HYMN) the script requires.
     * @param key The script's graph key (campaign key, scope tag).
     * @param references The mission's SDK rows; empty fills every name with a placeholder.
     */
    public static string Create(MissionActivityType type, string missionKey, string key, IReadOnlyList<MissionSdkReference> references)
    {
        if (!LuaScript.IsIdentifier(missionKey)) throw new InvalidDataException("Choose a mission from missions.lua.");
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)) throw new InvalidDataException("Use a nonempty graph key.");
        var names = new Names(references);
        string header = $"-- {Types.First(info => info.Type == type).Label}, made by Solforge's New graph. Not tested in game.\n"
            + "-- Replace every CHANGE_ME (Checks lists them) with names from this mission's Slot, Squad and Directive tables.\n"
            + "local missions = require(\"missions\")\n"
            + $"local mission = require(missions.{missionKey})\n";
        string body = type switch
        {
            MissionActivityType.Patrol or MissionActivityType.PublicEvent or MissionActivityType.LostSector => Freeroam(type, names),
            MissionActivityType.Pvp => Pvp(key, names),
            MissionActivityType.Custom => Custom(names),
            _ => Campaign(type, key, names)
        };
        return header + body;
    }

    private static string Campaign(MissionActivityType type, string key, Names names)
    {
        bool library = UsesMissionLibrary(type);
        var squads = names.Squads(type == MissionActivityType.Encounter ? 1 : 6);
        int steps = type == MissionActivityType.Encounter ? 1 : 3;
        // Spread the squads over the steps; the last step is the boss (or the only encounter).
        var groups = Enumerable.Range(0, steps).Select(_ => new List<string>()).ToList();
        for (int i = 0; i < squads.Count; i++) groups[Math.Min(steps - 1, i * steps / Math.Max(1, squads.Count))].Add(squads[i]);
        foreach (var group in groups.Where(group => group.Count == 0)) group.Add(Placeholder + "_SQUAD");
        string[] ids = type switch
        {
            MissionActivityType.Encounter => ["encounter"],
            MissionActivityType.Raid => ["encounter_1", "encounter_2", "final"],
            MissionActivityType.Quest => ["travel", "collect", "finish"],
            MissionActivityType.Strike or MissionActivityType.StrikeBoss => ["approach", "push", "boss"],
            MissionActivityType.RaidEncounter => ["mechanic", "damage", "final_stand"],
            _ => ["arrive", "fight", "boss"]
        };
        string objective = names.Slot(slot => slot.StartsWith("OBJ_", StringComparison.Ordinal) || slot.Contains("OBJECTIVE"), "OBJECTIVE");
        var text = new StringBuilder();
        text.Append("local campaign = require(\"lib.campaign\")\n");
        if (library) text.Append(Solforge.Graphs.MissionLibrary.Require + "\n");
        text.Append("local unit = campaign.unit\n");
        text.Append("local Slot, Squad, Directive = mission.Slot, mission.Squad, mission.Directive\n\n");
        for (int i = 0; i < steps; i++)
        {
            text.Append($"local {ids[i]} = {{\n    id = \"{ids[i]}\", ");
            if (i > 0) text.Append($"after = \"{ids[i]}\", ");
            text.Append($"objective = Slot.{objective},\n    squads = {{\n");
            foreach (string squad in groups[i]) text.Append($"        unit(Squad.{squad}, Slot.{squad}),\n");
            text.Append("    },\n}\n");
        }
        text.Append("\nreturn campaign.new{\n");
        text.Append($"    key = {LuaScript.Quote(key)},\n");
        text.Append($"    directive_sensor = Slot.{names.Slot(slot => slot.Contains("DIRECTIVE_SENSOR"), "DIRECTIVE_SENSOR")},\n");
        // Only steps with lines need a dialogue sensor; the template has none, so it is left out when the SDK has none.
        string dialogue = names.Slot(slot => slot.Contains("DIALOG_SENSOR") || slot.Contains("DIALOGUE_SENSOR"), "");
        if (!dialogue.StartsWith(Placeholder, StringComparison.Ordinal)) text.Append($"    dialogue_sensor = Slot.{dialogue},\n");
        text.Append("    legs = {\n");
        text.Append($"        {{id = \"start\", state = mission.states.{names.State()}}},\n");
        text.Append("    },\n    steps = {\n");
        var directives = names.Directives(steps);
        for (int i = 0; i < steps; i++)
        {
            bool last = i == steps - 1;
            text.Append($"        {{id = \"{ids[i]}\", directive = Directive.{directives[i]}");
            if (type == MissionActivityType.Quest && i == 1)
                text.Append(", counter = {of = 3}, ends = {clear = {\"" + ids[i] + "\"}}");
            else text.Append((last && type != MissionActivityType.Quest || type is MissionActivityType.Raid or MissionActivityType.RaidEncounter ? ", barrier = true" : "")
                + ", ends = {clear = {\"" + ids[i] + "\"}}");
            if (library && LibraryStep(type, i, names) is { } call)
                text.Append(",\n            on_start = function(context)\n                ").Append(call).Append("\n            end,\n        ");
            text.Append("},\n");
        }
        text.Append("    },\n");
        text.Append($"    encounters = {{{string.Join(", ", ids)}}},\n");
        if (library)
        {
            // The ordered ending runs from campaign's finish hook: final cutscene, fade, rewards, then completion.
            var ending = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cutscene"] = type == MissionActivityType.StrikeBoss ? "" : "Slot." + names.Slot(IsCinematic, "CINEMATIC"),
                ["experience"] = type == MissionActivityType.RaidEncounter ? "5000" : "2500", ["loot_rolls"] = "1",
            };
            text.Append("    finish = function(context)\n        ").Append(Lib("ending", ending)).Append("\n    end,\n");
        }
        text.Append("}\n");
        return text.ToString();
    }

    /** True for the templates that call lib.mission_lib (cutscenes, transitions, boss phases, ordered endings). */
    public static bool UsesMissionLibrary(MissionActivityType type) =>
        type is MissionActivityType.CampaignCinematic or MissionActivityType.StrikeBoss or MissionActivityType.RaidEncounter;

    private static bool IsCinematic(string slot) => slot.Contains("CINEMATIC") || slot.Contains("CUTSCENE");

    private static string Lib(string function, IReadOnlyDictionary<string, string> values) =>
        Solforge.Graphs.MissionLibrary.Render(Solforge.Graphs.MissionLibrary.Find(function)
            ?? throw new InvalidOperationException("The mission library has no " + function + "."), values);

    /** The mission-library call a step starts with, or null; slot names come from the SDK or are CHANGE_ME. */
    private static string? LibraryStep(MissionActivityType type, int step, Names names)
    {
        string Boss() => "Slot." + names.Slot(slot => slot.Contains("BOSS"), "BOSS");
        string Pylons()
        {
            var pylons = names.Slots(slot => slot.Contains("PYLON"), 3);
            if (pylons.Count == 0) pylons.Add(Placeholder + "_PYLON");
            return "{" + string.Join(", ", pylons.Select(slot => "Slot." + slot)) + "}";
        }
        string Transition() => Lib("transition", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["teleport"] = "Slot." + names.Slot(slot => slot.Contains("SPAWN"), "SPAWN_POINT"), ["state"] = "mission.states." + names.State(),
        });
        return (type, step) switch
        {
            (MissionActivityType.CampaignCinematic, 0) => Lib("play_cutscene", new Dictionary<string, string>(StringComparer.Ordinal)
                { ["cinematic"] = "Slot." + names.Slot(IsCinematic, "CINEMATIC") }),
            (MissionActivityType.CampaignCinematic, 1) => Transition(),
            (MissionActivityType.StrikeBoss, 2) => Lib("boss_phase", new Dictionary<string, string>(StringComparer.Ordinal)
                { ["boss"] = Boss(), ["pylons"] = Pylons() }),
            (MissionActivityType.RaidEncounter, 0) => Lib("set_invulnerable", new Dictionary<string, string>(StringComparer.Ordinal) { ["target"] = Boss() }),
            (MissionActivityType.RaidEncounter, 1) => Lib("boss_phase", new Dictionary<string, string>(StringComparer.Ordinal)
                { ["boss"] = Boss(), ["at_health"] = "1", ["pylons"] = Pylons() }),
            (MissionActivityType.RaidEncounter, 2) => Transition(),
            _ => null
        };
    }

    private static string Freeroam(MissionActivityType type, Names names)
    {
        var text = new StringBuilder();
        text.Append("local freeroam = require(\"lib.freeroam\")\n");
        text.Append("local unit = freeroam.unit\n");
        text.Append("local Slot, Squad = mission.Slot, mission.Squad\n\n");
        text.Append("local zones = {\n    {\n");
        text.Append($"        state = mission.states.{names.State()},\n");
        text.Append("        region = 0,\n");
        var squads = names.Squads(4);
        if (squads.Count == 0) squads.Add(Placeholder + "_SQUAD");
        string Units(string indent) => string.Concat(squads.Select(squad => $"{indent}unit(Squad.{squad}, Slot.{squad}),\n"));
        switch (type)
        {
            case MissionActivityType.Patrol:
                text.Append("        patrol = {\n").Append(Units("            ")).Append("        },\n");
                break;
            case MissionActivityType.PublicEvent:
                text.Append("        events = {\n            {\n");
                text.Append($"                sensor = Slot.{names.Slot(slot => slot.Contains("PUBLIC_EVENT_SENSOR"), "PUBLIC_EVENT_SENSOR")},\n");
                text.Append($"                objective = Slot.{names.Slot(slot => slot.Contains("OBJ"), "EVENT_OBJECTIVE")},\n");
                text.Append("                squads = {\n").Append(Units("                    ")).Append("                },\n");
                text.Append($"                chest = {{Slot.{names.Slot(slot => slot.Contains("CHEST"), "CHEST")}}},\n");
                text.Append("            },\n        },\n");
                break;
            default:
                text.Append("        -- Lost sector: the boss kill clears it and opens the chest.\n");
                text.Append("        sector = {\n");
                text.Append($"            boss = Slot.{names.Slot(slot => slot.Contains("BOSS"), "BOSS_SQUAD")},\n");
                text.Append("            squads = {\n").Append(Units("                ")).Append("            },\n");
                text.Append($"            chest = {{Slot.{names.Slot(slot => slot.Contains("CHEST"), "CHEST")}}},\n");
                text.Append("        },\n");
                break;
        }
        text.Append("    },\n}\n\n");
        text.Append("return freeroam.new{name = mission.name, mission = mission, zones = zones}\n");
        return text.ToString();
    }

    private static string Pvp(string key, Names names) =>
        "local lib = require(\"lib.mission_lib\")\n\n"
        + $"local TAG = {LuaScript.Quote(key)}\n"
        + "local ROUND_MS = 600000\n\n"
        + "local function start_round(context, state)\n"
        + "    local scope = lib.scope(context, state, TAG)\n"
        + "    scope:set_variable(\"score\", 0)\n"
        + "    scope:start_timer(\"round\", ROUND_MS)\n"
        + "end\n\n"
        + "return {\n"
        + $"    initial_state = {{region_index = mission.states.{names.State()}.region_index}},\n"
        + "    on_start = start_round,\n"
        + "    on_load = function(context, state) end,\n"
        + "    on_event_timer_elapsed = function(context, state, event)\n"
        + "        if lib.timer_name(TAG, event.timer_name) ~= \"round\" then return end\n"
        + "        lib.grant_experience(context, 1000, TAG .. \" round\")\n"
        + "        context:complete_mission{}\n"
        + "    end,\n"
        + "}\n";

    private static string Custom(Names names) =>
        "\nreturn {\n"
        + $"    initial_state = {{region_index = mission.states.{names.State()}.region_index}},\n"
        + "    on_start = function(context, state) end,\n"
        + "    on_load = function(context, state) end,\n"
        + "}\n";

    private static readonly Regex SdkName = new(@"\b(?:mission\.)?(Slot|Squad|Directive|states)\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant);

    /**
     * Placeholders left in a script, and Slot/Squad/Directive/states names the mission's SDK rows do not have. A table
     * with no SDK rows (the SDK did not read) is not checked, so a missing SDK gives no false findings.
     */
    public static IReadOnlyList<MissionTemplateFinding> Check(string source, IReadOnlyList<MissionSdkReference> references)
    {
        var findings = new List<MissionTemplateFinding>();
        var known = references.GroupBy(row => row.Table, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Key).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var lines = source.Split('\n');
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            int comment = line.IndexOf("--", StringComparison.Ordinal);
            string code = comment >= 0 ? line[..comment] : line;
            foreach (Match match in SdkName.Matches(code))
            {
                string table = "mission." + match.Groups[1].Value, name = match.Groups[2].Value;
                if (!seen.Add(table + "." + name)) continue;
                if (name.StartsWith(Placeholder, StringComparison.Ordinal))
                    findings.Add(new($"Line {index + 1}: replace {match.Value} with a name from {table}.", index + 1));
                else if (known.TryGetValue(table, out var names) && !names.Contains(name))
                    findings.Add(new($"Line {index + 1}: {match.Value} is not in this mission's {table} (the open map has no such {match.Groups[1].Value.TrimEnd('s').ToLowerInvariant()}).", index + 1));
            }
        }
        return findings;
    }

    /** SDK names by table, picked in a stable order. */
    private sealed class Names(IReadOnlyList<MissionSdkReference> references)
    {
        private IEnumerable<string> Keys(string table) => references.Where(row => row.Table == table).Select(row => row.Key).Distinct();

        /** First state key, else a placeholder. */
        public string State() => Keys("mission.states").FirstOrDefault() ?? Placeholder + "_STATE";

        /** First slot key matching the filter, else a placeholder. */
        public string Slot(Func<string, bool> match, string what) => Keys("mission.Slot").FirstOrDefault(match) ?? Placeholder + "_" + what;

        /** Up to count slot keys matching the filter. */
        public List<string> Slots(Func<string, bool> match, int count) => Keys("mission.Slot").Where(match).Take(count).ToList();

        /** Squads that also have a slot of the same key, as freeroam.unit and campaign.unit need. */
        public List<string> Squads(int count)
        {
            var slots = Keys("mission.Slot").ToHashSet(StringComparer.Ordinal);
            return Keys("mission.Squad").Where(slots.Contains).Take(count).ToList();
        }

        /** Directive keys for a new node; missing ones repeat the last key or a placeholder. */
        public string[] Directives(int count)
        {
            var found = Keys("mission.Directive").Take(count).ToList();
            while (found.Count < count) found.Add(found.Count > 0 ? found[^1] : Placeholder + "_DIRECTIVE");
            return found.ToArray();
        }
    }
}
