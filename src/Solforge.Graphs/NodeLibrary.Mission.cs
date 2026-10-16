namespace Solforge.Graphs;

/**
 * Mission graph nodes. They compile to a Sunrise mission script module: a Lua table of on_start, on_load and
 * on_event_* handlers taking (context, state, event), as MissionScriptFactory writes. Every SDK call is one the
 * server's Lua context binds (mission_script_lua_context_api.cpp), called directly.
 */
public static partial class NodeLibrary
{
    /** Activity callbacks a script module may define; each on_event_* kind is one mission_api_events.h names. */
    public static readonly string[] MissionEvents =
    [
        "on_start", "on_load", "region_changed", "client_state_changed", "player_trigger", "squad_state", "scene_finished",
        "scene_output", "timer_elapsed", "actor_path_state", "fireteam_state", "effect_result", "damage_state", "object_state",
        "object_interacted", "device_state", "ghost_link_state", "trigger_entered", "trigger_exited", "entity_died",
        "entity_spawned", "objective_progress", "phase_entered", "session_joined", "session_left", "squad_provoked",
        "client_message_received", "sensor_sense_updated"
    ];

    /** Context methods the server binds as functions; Call SDK Function offers only these. */
    public static readonly string[] MissionContextFunctions =
    [
        "log", "complete_mission", "activate_objects", "grant_reward", "report_progress", "cohort", "squad", "scene", "slot",
        "hold_spawn", "select_state", "restart_checkpoint", "set_phase", "set_variable", "clear_variable", "start_timer", "cancel_timer"
    ];

    /** Node types earlier builds offered for SDK functions the server does not have, and what replaces them. */
    public static readonly IReadOnlyDictionary<string, string> RemovedMissionNodes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["mission.set_objective"] = "The server has no set_objective; objectives are directives: use Set Directive.",
        ["mission.spawn_squad"] = "Replaced by Place Squad (context:squad(id):place{}).",
        ["entity.get_player"] = "The server has no get_player; mission scripts cannot read a player entity.",
        ["entity.find_named_actor"] = "Replaced by Get Slot (context:slot(id)).",
        ["entity.filter_by_health"] = "The server has no get_health_fraction; use a damage_state Activity Event."
    };

    /** Signals the Phase 31 phase nodes stand for; any other name works too. */
    public static readonly string[] MissionSignals = ["ObjectiveUpdated", "CombatWaveSpawned", "BossVulnerable", "EncounterComplete"];

    private static IEnumerable<NodeType> MissionTypes()
    {
        const GraphKind kind = GraphKind.Mission;
        PinDef eventOut = Out("event", "Event", PinType.Any);
        // Events.
        yield return Node("mission.start", "Mission Start", "Events", kind, "Runs when the activity starts (on_start).",
            [Then], keywords: "begin on_start");
        yield return Node("mission.event", "Activity Event", "Events", kind, "Runs on an activity callback; Event is the callback's event table.",
            [Then, eventOut], [Prop("event", "Event", PropertyKind.Choice, "region_changed", "", MissionEvents)], "callback on_event");
        yield return Node("mission.on_signal", "On Signal", "Events", kind,
            "Runs when Send Signal fires this name (ObjectiveUpdated, CombatWaveSpawned, BossVulnerable or your own).",
            [Then], [Prop("signal", "Signal", PropertyKind.Choice, "ObjectiveUpdated", "Signal name", MissionSignals)],
            "phase objective wave boss custom event");

        // Flow.
        yield return Node("flow.branch", "Branch", "Flow", kind, "Takes True or False by Condition.",
            [ExecIn, In("condition", "Condition", PinType.Bool), Out("true", "True", PinType.Exec), Out("false", "False", PinType.Exec)],
            keywords: "if else");
        yield return Node("flow.sequence", "Sequence", "Flow", kind, "Runs Then 0, Then 1 and Then 2 in order.",
            [ExecIn, Out("then0", "Then 0", PinType.Exec), Out("then1", "Then 1", PinType.Exec), Out("then2", "Then 2", PinType.Exec)],
            keywords: "order steps");
        yield return Node("flow.parallel_all", "Parallel All", "Flow", kind,
            "Starts every branch, then runs Completed once each branch has reached a Finish Branch node.",
            [ExecIn, Out("a", "Branch A", PinType.Exec), Out("b", "Branch B", PinType.Exec), Out("completed", "Completed", PinType.Exec)],
            keywords: "join fork concurrent");
        yield return Node("flow.finish_branch", "Finish Branch", "Flow", kind, "Marks one branch of a Parallel All as done.",
            [ExecIn], [Prop("parallel", "Parallel node id", PropertyKind.Text)], "join done");
        yield return Node("flow.delay", "Delay", "Flow", kind,
            "Continues after Seconds on a server mission timer (context:start_timer), so it survives a save and reload. Running it again restarts it.",
            [ExecIn, In("seconds", "Seconds", PinType.Float, "1"), Out("completed", "Completed", PinType.Exec)], keywords: "timer wait seconds");
        yield return Node("flow.wait_signal", "Wait Signal", "Flow", kind, "Continues when Send Signal fires the name.",
            [ExecIn, Out("then", "Then", PinType.Exec)], [Prop("signal", "Signal", PropertyKind.Choice, "ObjectiveUpdated", "", MissionSignals)],
            keywords: "await event");
        yield return Node("flow.send_signal", "Send Signal", "Flow", kind, "Fires a signal: On Signal handlers and waiting branches run.",
            [ExecIn, Then], [Prop("signal", "Signal", PropertyKind.Choice, "ObjectiveUpdated", "", MissionSignals)], "emit raise fire");
        yield return Node("flow.do_once", "Do Once", "Flow", kind, "Passes the first time only; kept in the mission state, so a reload remembers it.",
            [ExecIn, Then], keywords: "once gate");

        // State.
        yield return Node("state.set_flag", "Set Flag", "State", kind, "Stores a named boolean as a mission variable (kept across save and reload).",
            [ExecIn, In("value", "Value", PinType.Bool, "true"), Then], [Prop("name", "Name", PropertyKind.Text, "flag")], "variable set");
        yield return Node("state.get_flag", "Get Flag", "State", kind, "Reads a named boolean mission variable (false when unset).",
            [Out("value", "", PinType.Bool)], [Prop("name", "Name", PropertyKind.Text, "flag")], "variable get");
        yield return Node("state.set_number", "Set Number", "State", kind, "Stores a named number as a mission variable (kept across save and reload).",
            [ExecIn, In("value", "Value", PinType.Float, "0"), Then], [Prop("name", "Name", PropertyKind.Text, "count")], "variable counter");
        yield return Node("state.get_number", "Get Number", "State", kind, "Reads a named number mission variable (0 when unset).",
            [Out("value", "", PinType.Float)], [Prop("name", "Name", PropertyKind.Text, "count")], "variable counter");
        yield return Node("event.field", "Event Field", "State", kind, "Reads a field of the callback's event, e.g. kind or timer_name.",
            [In("event", "Event", PinType.Any), Out("value", "", PinType.Any)], [Prop("field", "Field", PropertyKind.Text, "kind")], "table get member");
        yield return Node("state.get_phase", "Get Phase", "State", kind, "The mission phase number (state.phase).",
            [Out("value", "", PinType.Int)], keywords: "stage step");

        // Entities: squad and slot handles the server resolves by id or name.
        yield return Node("entity.get_squad", "Get Squad", "Entities", kind, "A squad handle: context:squad(id or slot name).",
            [Out("squad", "", PinType.Entity)], [Prop("name", "Squad", PropertyKind.Text)], "enemies combatants");
        yield return Node("entity.slot", "Get Slot", "Entities", kind, "An object slot handle: context:slot(id or name).",
            [Out("slot", "", PinType.Entity)], [Prop("name", "Slot", PropertyKind.Text)], "actor object named find");

        // Actions.
        yield return Node("mission.set_directive", "Set Directive", "Actions", kind,
            "Shows an objective: the generated mission module's Directive on its sensor slot (or Sensor slot when given).",
            [ExecIn, Then], [Prop("module", "Mission module", PropertyKind.Text, "", "missions.lua key, e.g. EDZ_FREEROAM"),
             Prop("directive", "Directive", PropertyKind.Text, "", "Directive name in that module"),
             Prop("sensor", "Sensor slot", PropertyKind.Text, "", "Optional; the directive's own sensor when empty")],
            "objective quest step hud set_directive");
        yield return Node("mission.place_squad", "Place Squad", "Actions", kind, "Places (spawns) a squad: context:squad(id):place{}.",
            [ExecIn, Then, Out("squad", "Squad", PinType.Entity)], [Prop("name", "Squad", PropertyKind.Text)], "spawn combat wave enemies");
        yield return Node("mission.fire_trigger", "Fire Trigger", "Actions", kind, "Fires a trigger slot: context:slot(id):fire_trigger().",
            [ExecIn, Then], [Prop("slot", "Trigger slot", PropertyKind.Text)], "arm trigger");
        yield return Node("mission.set_phase", "Set Phase", "Actions", kind, "Sets the mission phase number (context:set_phase).",
            [ExecIn, In("phase", "Phase", PinType.Int, "1"), Then], keywords: "stage step");
        yield return Node("mission.complete", "Complete Mission", "Actions", kind, "Completes the mission (context:complete_mission{}).",
            [ExecIn, Then], keywords: "finish win end");
        yield return Node("mission.log", "Log", "Actions", kind, "Writes to the server log (context:log, 400 characters at most).",
            [ExecIn, In("text", "Text", PinType.String), Then], keywords: "print debug");
        yield return Node("mission.call", "Call SDK Function", "Actions", kind,
            "Calls context:<function>(args...); Result is its first return value until the activity reloads.",
            [ExecIn, In("a1", "Arg 1", PinType.Any), In("a2", "Arg 2", PinType.Any), In("a3", "Arg 3", PinType.Any), Then,
             Out("result", "Result", PinType.Any)], [Prop("function", "Function", PropertyKind.Choice, "log", "", MissionContextFunctions)], "sdk api invoke");
        yield return Node("mission.lua", "Lua", "Actions", kind, "Raw Lua run in the handler (context, state and event are in scope).",
            [ExecIn, Then], [Prop("code", "Code", PropertyKind.Code, "-- lua")], "script snippet raw custom");
    }
}
