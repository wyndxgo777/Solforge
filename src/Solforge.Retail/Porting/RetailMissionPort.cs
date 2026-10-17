/**
 * File: Mission port. Turns a retail activity into what Sol can use today: a content/activity document (one objective
 * per retail phase, chained in order), a server Lua skeleton in the lib.campaign / lib.freeroam shape Solforge's mission
 * templates use (CHANGE_ME placeholders where Sol names are needed), and a report of what was and was not ported.
 */
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Solforge.Content;
using Solforge.Retail.Decoding;

namespace Solforge.Retail.Porting;

/** The result of a mission port: the activity document, the Lua skeleton, and the report (JSON). */
public sealed record RetailMissionPortResult(ActivityDefinition Activity, string ScriptStem, string Script, JsonObject Report);

/** Builds mission ports. Nothing here writes files; RetailMissionPort.Write does, into a project folder. */
public static class RetailMissionPort
{
    public const string Placeholder = "CHANGE_ME";

    /** Guesses the Sol activity type from a retail activity name ("schism.strike_pickaxe" -> Strike). */
    public static ActivityType TypeOf(string? name)
    {
        string text = (name ?? "").ToLowerInvariant();
        string leaf = text.Contains('.') ? text[(text.LastIndexOf('.') + 1)..] : text;
        if (leaf.Contains("freeroam") || leaf.Contains("ambient") || leaf.Contains("patrol")) return ActivityType.Patrol;
        if (leaf.Contains("strike") || leaf.Contains("nightfall")) return leaf.Contains("nightfall") ? ActivityType.Nightfall : ActivityType.Strike;
        if (leaf.Contains("raid")) return ActivityType.Raid;
        if (leaf.Contains("dungeon")) return ActivityType.Dungeon;
        if (leaf.EndsWith("_ls", StringComparison.Ordinal) || leaf.Contains("_ls_") || leaf.Contains("lost_sector")) return ActivityType.LostSector;
        if (leaf.Contains("crucible") || leaf.Contains("pvp") || text.StartsWith("crucible", StringComparison.Ordinal)) return ActivityType.Crucible;
        if (leaf.Contains("gambit")) return ActivityType.Gambit;
        if (leaf.Contains("public_event") || leaf.Contains("event")) return ActivityType.PublicEvent;
        if (leaf.Contains("mission") || leaf.Contains("campaign") || leaf.Contains("story")) return ActivityType.Story;
        return ActivityType.Custom;
    }

    /** A content id from a retail name: "schism.mission_alice" -> "retail_schism_mission_alice". */
    public static string IdOf(string? name, uint tag)
    {
        var text = new StringBuilder("retail_");
        foreach (char c in (name ?? tag.ToString("x8", CultureInfo.InvariantCulture)).ToLowerInvariant())
            text.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_');
        string id = text.ToString().Trim('_');
        while (id.Contains("__", StringComparison.Ordinal)) id = id.Replace("__", "_", StringComparison.Ordinal);
        return id.Length <= 64 ? id : id[..64].TrimEnd('_');
    }

    /** Ports an activity tag. names resolves FNV hashes (bubble names) when the engine knows them; it may return null. */
    public static RetailMissionPortResult? Port(RetailSource source, uint activityTag, out string? problem,
        IReadOnlyDictionary<uint, string>? names = null, bool readBubbles = true)
    {
        var activity = RetailActivity.ReadActivity(source, activityTag, out problem);
        if (activity is null) return null;
        var destination = activity.Destination is { } destinationTag ? RetailActivity.ReadDestination(source, destinationTag, out _) : null;
        string? name = activity.Name ?? destination?.Activities.FirstOrDefault(row => row.Hash == activity.ActivityHash)?.Name;
        string Hash(uint hash) => names?.GetValueOrDefault(hash) ?? $"0x{hash:X8}";
        var type = TypeOf(name);
        string id = IdOf(name, activityTag);

        var report = new JsonObject
        {
            ["activity"] = RetailTag.Hex(activityTag), ["name"] = name, ["sol_type"] = type.ToString(),
            ["destination"] = activity.Destination is { } d ? RetailTag.Hex(d) : null, ["destination_name"] = destination?.Name,
        };
        var phases = new JsonArray();
        var objectives = new List<ActivityObjective>();
        var unported = new JsonArray();
        int totalEntities = 0;
        foreach (var phase in activity.Phases)
        {
            int entities = phase.Entities.Sum(link => link.EntityResources.Count);
            totalEntities += entities;
            var bubbles = new JsonArray();
            foreach (uint parent in phase.BubbleParents)
            {
                var bubble = new JsonObject { ["bubble_parent"] = RetailTag.Hex(parent) };
                if (readBubbles)
                {
                    if (RetailMap.ReadBubble(source, parent, out string? why, maxNodes: 20_000) is { } map)
                    {
                        bubble["nodes"] = map.Nodes.Count;
                        bubble["components"] = new JsonObject(map.KindCounts.Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)));
                    }
                    else bubble["problem"] = why;
                }
                bubbles.Add(bubble);
            }
            string objectiveId = $"phase_{phase.Index + 1}";
            objectives.Add(new ActivityObjective
            {
                Id = objectiveId, Kind = phase.Index == 0 ? "travel" : "custom",
                Description = $"Retail phase {phase.Index + 1}: bubble {Hash(phase.BubbleHash)}, {phase.BubbleParents.Count} map references, {entities} entity resources.",
            });
            phases.Add(new JsonObject
            {
                ["index"] = phase.Index, ["bubble"] = Hash(phase.BubbleHash), ["bubble_hash"] = $"0x{phase.BubbleHash:X8}",
                ["map_references"] = bubbles, ["unresolved_map_references"] = phase.UnresolvedMaps,
                ["entity_links"] = phase.Entities.Count, ["entity_resources"] = entities,
                ["objective"] = objectiveId,
            });
        }
        if (objectives.Count == 0) objectives.Add(new ActivityObjective { Id = "start", Kind = "custom", Description = "The retail activity has no phases." });
        for (int i = 0; i + 1 < objectives.Count; i++) objectives[i].Next.Add(objectives[i + 1].Id);
        report["phases"] = phases;

        var document = new ActivityDefinition
        {
            Id = id, Name = name ?? $"Retail activity {RetailTag.Hex(activityTag)}", Type = type,
            Description = $"Ported from retail Destiny 2 build {RetailOptions.Build} activity {RetailTag.Hex(activityTag)} by Solforge. Objectives mirror the retail phases; fill in encounters, rewards and map/mission tags for Sol.",
            Destination = destination?.Name ?? "", Source = "retail:" + RetailTag.Hex(activityTag), Tags = ["retail-port", "build-" + RetailOptions.Build],
            FireteamMax = type switch { ActivityType.Raid or ActivityType.Crucible => 6, ActivityType.Gambit => 4, ActivityType.Patrol or ActivityType.PublicEvent => 9, _ => 3 },
            Start = objectives[0].Id, Objectives = objectives,
        };

        string stem = id.Replace('.', '_');
        string script = Script(type, stem, name, activityTag, activity, Hash);
        var issues = document.Validate();
        report["ported"] = new JsonArray
        {
            (JsonNode)$"content/activity/{id}.json: Sol activity document with {objectives.Count} objectives (one per retail phase)",
            (JsonNode)$"retail_ports/{stem}/{stem}.lua: mission script skeleton ({(IsFreeroam(type) ? "lib.freeroam" : "lib.campaign")}), {Placeholder} where Sol slot/squad names go",
        };
        unported.Add($"Encounters and squads: {totalEntities} retail entity resources (0x80809B06) are listed per phase but not decoded or converted.");
        unported.Add("Map geometry and placements: retail bubbles open read-only in the Retail workspace; Sol map packages are not written. Pick a Sol map/mission and set map_tag/mission_tag.");
        unported.Add("Dialogue and objective text: retail strings decode (retail_strings) but are not linked to objectives; the destination string container " +
            (destination?.StringContainer is { } strings ? RetailTag.Hex(strings) : "does not resolve") + ".");
        unported.Add("Scripting: retail activity logic runs in the game's own scripts and server; the Lua skeleton is a starting point, not a translation.");
        unported.Add("Rewards, modifiers, matchmaking and director data are not decoded.");
        report["not_ported"] = unported;
        report["content_issues"] = new JsonArray(issues.Select(issue => (JsonNode)$"{(issue.Error ? "error" : "warning")} {issue.Path}: {issue.Message}").ToArray());
        report["next_steps"] = new JsonArray
        {
            (JsonNode)"create_mission_script with the matching type and a missions.lua key makes a real script from the installed SDK names; copy the phase comments across.",
            (JsonNode)("validate_content family=activity id=" + id),
        };
        report["notes"] = new JsonArray(activity.Notes.Concat(destination?.Notes ?? []).Select(note => (JsonNode)note).ToArray());
        return new RetailMissionPortResult(document, stem, script, report);
    }

    private static bool IsFreeroam(ActivityType type) => type is ActivityType.Patrol or ActivityType.PublicEvent or ActivityType.LostSector;

    /** The Lua skeleton (same shapes as SunriseStudio.Missions.MissionTemplates, with retail phases as steps). */
    private static string Script(ActivityType type, string stem, string? name, uint tag, RetailActivityInfo activity, Func<uint, string> hash)
    {
        var text = new StringBuilder();
        text.Append($"-- Retail port of {name ?? RetailTag.Hex(tag)} ({RetailTag.Hex(tag)}, build {RetailOptions.Build}) made by Solforge. Not tested in game.\n");
        text.Append($"-- Replace every {Placeholder} with names from the chosen Sol mission's Slot, Squad and Directive tables.\n");
        foreach (var phase in activity.Phases)
            text.Append($"-- phase {phase.Index + 1}: bubble {hash(phase.BubbleHash)}, maps {string.Join(", ", phase.BubbleParents.Select(RetailTag.Hex))}, " +
                $"{phase.Entities.Sum(link => link.EntityResources.Count)} entity resources\n");
        text.Append("local missions = require(\"missions\")\n");
        text.Append($"local mission = require(missions.{Placeholder})\n");
        if (IsFreeroam(type))
        {
            text.Append("local freeroam = require(\"lib.freeroam\")\nlocal unit = freeroam.unit\nlocal Slot, Squad = mission.Slot, mission.Squad\n\n");
            text.Append("local zones = {\n");
            int zones = Math.Max(1, activity.Phases.Count);
            for (int i = 0; i < zones; i++)
            {
                text.Append($"    {{ -- phase {i + 1}\n        state = mission.states.{Placeholder}_STATE,\n        region = {i},\n");
                text.Append($"        patrol = {{\n            unit(Squad.{Placeholder}_SQUAD, Slot.{Placeholder}_SQUAD),\n        }},\n    }},\n");
            }
            text.Append("}\n\nreturn freeroam.new{name = mission.name, mission = mission, zones = zones}\n");
            return text.ToString();
        }
        text.Append("local campaign = require(\"lib.campaign\")\nlocal unit = campaign.unit\nlocal Slot, Squad, Directive = mission.Slot, mission.Squad, mission.Directive\n\n");
        List<string> ids = activity.Phases.Count == 0 ? ["start"] : activity.Phases.Select(phase => $"phase_{phase.Index + 1}").ToList();
        for (int i = 0; i < ids.Count; i++)
        {
            text.Append($"local {ids[i]} = {{\n    id = \"{ids[i]}\", ");
            if (i > 0) text.Append($"after = \"{ids[i]}\", ");
            text.Append($"objective = Slot.{Placeholder}_OBJECTIVE,\n    squads = {{\n        unit(Squad.{Placeholder}_SQUAD, Slot.{Placeholder}_SQUAD),\n    }},\n}}\n");
        }
        text.Append("\nreturn campaign.new{\n");
        text.Append($"    key = \"{stem}\",\n    directive_sensor = Slot.{Placeholder}_DIRECTIVE_SENSOR,\n    legs = {{\n        {{id = \"start\", state = mission.states.{Placeholder}_STATE}},\n    }},\n    steps = {{\n");
        for (int i = 0; i < ids.Count; i++)
            text.Append($"        {{id = \"{ids[i]}\", directive = Directive.{Placeholder}_DIRECTIVE{(i == ids.Count - 1 ? ", barrier = true" : "")}, ends = {{clear = {{\"{ids[i]}\"}}}}}},\n");
        text.Append($"    }},\n    encounters = {{{string.Join(", ", ids)}}},\n}}\n");
        return text.ToString();
    }

    /** Writes the port into a project: content/activity/<id>.json and retail_ports/<stem>/<stem>.lua + report.json. */
    public static IReadOnlyList<string> Write(string projectRoot, RetailMissionPortResult result, bool overwrite)
    {
        var store = new ContentStore(projectRoot);
        string documentPath = store.PathOf(result.Activity.Family, result.Activity.Id);
        string folder = Path.Combine(projectRoot, "retail_ports", result.ScriptStem);
        string scriptPath = Path.Combine(folder, result.ScriptStem + ".lua"), reportPath = Path.Combine(folder, "report.json");
        if (!overwrite && (File.Exists(documentPath) || File.Exists(scriptPath)))
            throw new IOException($"{result.Activity.Id} was ported before; pass overwrite to replace it.");
        Directory.CreateDirectory(folder);
        string saved = store.Save(result.Activity);
        File.WriteAllText(scriptPath, result.Script, new UTF8Encoding(false));
        File.WriteAllText(reportPath, result.Report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        return [saved, scriptPath, reportPath];
    }
}
