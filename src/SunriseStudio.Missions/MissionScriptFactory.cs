namespace SunriseStudio.Missions;

public sealed record MissionSdkActivity(string Key, string Module);
public sealed record MissionScriptDraft(MissionScriptEntry Entry, MissionGraphDocument Graph);

public sealed partial class MissionScriptCatalog
{
    /** The key and module rows of an SDK index such as missions.lua; a missing or unreadable index gives none. */
    private IReadOnlyList<MissionSdkActivity> ReadModuleIndex(string filename)
    {
        string path = Path.Combine(SdkRoot, filename);
        if (!File.Exists(path)) { NoteSdkMissing(filename); return []; }
        if (TryReadSdk(filename, path) is not { } source) return [];
        var lua = LuaScript.Read(source);
        NoteSdkRead(filename, lua);
        if (FirstTable(lua) is not { } table) return [];
        return table.Fields.Where(field => field.Name is not null && field.StringValue is not null
            && field.StringValue.Split('.').All(LuaScript.IsIdentifier))
            .Select(field => new MissionSdkActivity(field.Name!, field.StringValue!)).ToArray();
    }

    /** A new script targets an existing SDK activity; it does not create a native activity package. */
    public MissionScriptEntry EntryForActivity(MissionSdkActivity activity)
    {
        if (!Activities.Contains(activity)) throw new InvalidDataException("Choose an installed SDK activity.");
        string path = Path.Combine(SdkRoot, activity.Module.Replace('.', Path.DirectorySeparatorChar) + ".lua");
        if (!File.Exists(path)) throw new InvalidDataException(NoteSdkMissing(activity.Module));
        var definition = FirstTable(LuaScript.Read(ReadSdkSource(path)));
        string? name = definition?.Field("name")?.StringValue;
        if (string.IsNullOrEmpty(name)) name = definition?.Field("display_name")?.StringValue;
        if (name is null) throw new InvalidDataException("SDK activity has no script name.");
        string? module = definition?.Field("mission")?.Expression is LuaCall
        {
            Target: LuaName { Name: "require" }, Method: null, Arguments: { Form: LuaArgumentsForm.List, Values: [LuaLiteral argument] }
        } ? argument.StringValue : null;
        if (module is null || !missionModules.Any(value => value.Module == module))
            throw new NotSupportedException("This activity has no generated mission SDK binding.");
        string stem = ControllerStem(name);
        return new(stem, stem + "/" + stem + ".lua", "", module);
    }

    /** Saved source chooses its own SDK module, including activities with no installed script. */
    public MissionScriptEntry DescribeSource(string activity, string relativePath, string source) =>
        new(activity, relativePath, "", ModuleForKey(new MissionScriptBindings(source).MissionKey));

    /** The same, from a document that is already parsed. */
    public MissionScriptEntry DescribeSource(string activity, string relativePath, MissionGraphDocument document) =>
        new(activity, relativePath, "", ModuleForKey(new MissionScriptBindings(document.Script).MissionKey));

    internal string MissionKeyForModule(string module) => missionModules.First(value => value.Module == module).Key;

    /** The runtime folds names into lowercase ASCII stems and collapses separator runs. */
    public static string ControllerStem(string name)
    {
        var output = new System.Text.StringBuilder(); bool separator = false;
        foreach (char character in name)
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (separator && output.Length != 0) output.Append('_');
                separator = false; output.Append(char.ToLowerInvariant(character));
            }
            else separator = true;
        if (output.Length == 0) output.Append("unnamed");
        if (char.IsAsciiDigit(output[0])) output.Insert(0, '_');
        if (output.Length >= 120) throw new InvalidDataException("Activity script name exceeds the runtime path limit.");
        return output.ToString();
    }
}

public static class MissionScriptFactory
{
    /** Why a new flow script cannot run here; empty when the installed scripts have lib/flow.lua. */
    public static string FlowRuntimeProblem(MissionScriptCatalog catalog) =>
        File.Exists(Path.Combine(catalog.ScriptsRoot, "lib", "flow.lua")) ? ""
            : "scripts/lib/flow.lua is missing. Update the mission scripts.";

    /** The starter forwards real callback events and waits for the activity's entered-region report. */
    public static MissionScriptDraft CreateFlow(MissionScriptCatalog catalog, MissionSdkActivity activity,
        MissionSdkReference initialState, string key)
    {
        var entry = catalog.EntryForActivity(activity);
        if (FlowRuntimeProblem(catalog) is { Length: > 0 } problem) throw new NotSupportedException(problem);
        // The tables Adopt passes, so both share one cached read.
        var references = catalog.ReadReferences(entry, catalog.Api?.MissionTables);
        if (!references.Contains(initialState) || !MissionVisualAuthoring.Accepts(initialState, MissionReferenceKind.State))
            throw new InvalidDataException("Choose an initial state from this activity.");
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)) throw new InvalidDataException("Use a nonempty graph key.");
        string state = initialState.Expression;
        string missionKey = catalog.MissionKeyForModule(entry.SdkModule!);
        string source = $$$"""
            local missions = require("missions")
            local mission = require(missions.{{{missionKey}}})
            local EventKind = require("sunrise.activity_sdk").EventKind
            local flow = require("lib.flow")

            local graph = flow.new{
                key = {{{LuaScript.Quote(key)}}},
                facts = {
                    {id = "arrival", observe = function(context, state, event)
                        return event.entered == true
                            and event.held_region_index == {{{state}}}.region_index
                    end},
                },
                steps = {
                    {id = "arrival", await = {fact = "arrival"}},
                    {id = "start", after = {"arrival"}, run = function(context, state) end},
                },
            }

            local function handle(context, state, event)
                graph:handle(context, state, event)
            end

            return {
                initial_state = {region_index = {{{state}}}.region_index},
                on_start = function(context, state) graph:advance(context, state) end,
                on_load = function(context, state) graph:advance(context, state) end,
                on_event_region_changed = handle,
                on_event_client_state_changed = function(context, state, event)
                    if event.entered == true then handle(context, state, event) end
                end,
                on_event_player_trigger = handle,
                on_event_squad_state = handle,
                on_event_scene_finished = handle,
                on_event_scene_output = handle,
                on_event_timer_elapsed = handle,
                on_event_actor_path_state = handle,
                on_event_fireteam_state = handle,
                on_event_effect_result = handle,
                on_event_damage_state = handle,
                on_event_object_state = handle,
                on_event_object_interacted = handle,
                on_event_device_state = handle,
                on_event_ghost_link_state = handle,
                on_event_trigger_entered = handle,
                on_event_trigger_exited = handle,
            }
            """ + "\n";
        var graph = MissionGraphDocument.Parse(source); graph.RequireValid();
        return new(entry, graph);
    }

    /**
     * A new script of any activity type from its template (MissionTemplates), for a mission in missions.lua. The script
     * lives at stem/stem.lua from its name; names come from that mission's SDK rows, so Checks validates it against
     * the map's slots and squads.
     */
    public static MissionSdkActivity FindMission(MissionScriptCatalog catalog, string missionKey)
    {
        // A key (STRIKE_HYMN) or a map-ish name ("strike hymn", "edz freeroam"); a name must match one mission only.
        string wanted = string.Concat((missionKey ?? "").Trim().Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_'));
        if (wanted.Length == 0) throw new InvalidDataException("Name the mission (a missions.lua key such as STRIKE_HYMN, or the map).");
        if (catalog.Missions.FirstOrDefault(value => value.Key.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
        var near = catalog.Missions.Where(value => value.Key.Contains(wanted, StringComparison.OrdinalIgnoreCase)).Take(6).ToArray();
        return near.Length == 1 ? near[0] : throw new InvalidDataException(near.Length == 0
            ? $"No installed mission matches {missionKey}; list_missions or list_mission_scripts names them."
            : $"{missionKey} matches several missions: {string.Join(", ", near.Select(value => value.Key))}.");
    }

    public static MissionScriptDraft CreateTemplate(MissionScriptCatalog catalog, MissionActivityType type, string missionKey, string name)
    {
        var row = FindMission(catalog, missionKey);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Name the script.");
        string stem = MissionScriptCatalog.ControllerStem(name);
        var entry = new MissionScriptEntry(stem, stem + "/" + stem + ".lua", "", row.Module);
        var references = catalog.ReadReferences(entry, catalog.Api?.MissionTables);
        var graph = MissionGraphDocument.Parse(MissionTemplates.Create(type, row.Key, stem, references));
        graph.RequireValid();
        return new(entry, graph);
    }
}
