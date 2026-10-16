namespace Solforge.Graphs;

/**
 * Mission graph nodes. They compile to a Sunrise mission script module: a Lua table of on_start, on_load and
 * on_event_* handlers taking (context, state, event), as MissionScriptFactory writes. SDK calls go through a
 * guarded helper so a function missing on this build is skipped rather than crashing the activity host.
 */
public static partial class NodeLibrary
{
    /** Activity callbacks a script module may define (MissionScriptFactory's starter lists the same events). */
    public static readonly string[] MissionEvents =
    [
        "on_start", "on_load", "region_changed", "client_state_changed", "player_trigger", "squad_state", "scene_finished",
        "scene_output", "timer_elapsed", "actor_path_state", "fireteam_state", "effect_result", "damage_state", "object_state",
        "object_interacted", "device_state", "ghost_link_state", "trigger_entered"
    ];

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
            "Continues on the next timer_elapsed callback at or after Seconds (activity time from the event, else the next tick).",
            [ExecIn, In("seconds", "Seconds", PinType.Float, "1"), Out("completed", "Completed", PinType.Exec)], keywords: "timer wait seconds");
        yield return Node("flow.wait_signal", "Wait Signal", "Flow", kind, "Continues when Send Signal fires the name.",
            [ExecIn, Out("then", "Then", PinType.Exec)], [Prop("signal", "Signal", PropertyKind.Choice, "ObjectiveUpdated", "", MissionSignals)],
            keywords: "await event");
        yield return Node("flow.send_signal", "Send Signal", "Flow", kind, "Fires a signal: On Signal handlers and waiting branches run.",
            [ExecIn, Then], [Prop("signal", "Signal", PropertyKind.Choice, "ObjectiveUpdated", "", MissionSignals)], "emit raise fire");
        yield return Node("flow.do_once", "Do Once", "Flow", kind, "Passes the first time only (per activity run).",
            [ExecIn, Then], keywords: "once gate");

        // State.
        yield return Node("state.set_flag", "Set Flag", "State", kind, "Stores a named boolean in the script state.",
            [ExecIn, In("value", "Value", PinType.Bool, "true"), Then], [Prop("name", "Name", PropertyKind.Text, "flag")], "variable set");
        yield return Node("state.get_flag", "Get Flag", "State", kind, "Reads a named boolean from the script state.",
            [Out("value", "", PinType.Bool)], [Prop("name", "Name", PropertyKind.Text, "flag")], "variable get");
        yield return Node("state.set_number", "Set Number", "State", kind, "Stores a named number in the script state.",
            [ExecIn, In("value", "Value", PinType.Float, "0"), Then], [Prop("name", "Name", PropertyKind.Text, "count")], "variable counter");
        yield return Node("state.get_number", "Get Number", "State", kind, "Reads a named number from the script state.",
            [Out("value", "", PinType.Float)], [Prop("name", "Name", PropertyKind.Text, "count")], "variable counter");
        yield return Node("event.field", "Event Field", "State", kind, "Reads a field of an event table, e.g. entered or held_region_index.",
            [In("event", "Event", PinType.Any), Out("value", "", PinType.Any)], [Prop("field", "Field", PropertyKind.Text, "entered")], "table get member");

        // Entities (Phase 31).
        yield return Node("entity.get_player", "Get Player", "Entities", kind, "A player of the fireteam by index (1 first).",
            [In("index", "Index", PinType.Int, "1"), Out("player", "", PinType.Entity)], keywords: "guardian fireteam");
        yield return Node("entity.get_squad", "Get Squad", "Entities", kind, "A squad by name.",
            [Out("squad", "", PinType.Entity)], [Prop("name", "Squad", PropertyKind.Text)], "enemies combatants");
        yield return Node("entity.find_named_actor", "Find Named Actor", "Entities", kind, "An actor by its script name.",
            [Out("actor", "", PinType.Entity)], [Prop("name", "Name", PropertyKind.Text)], "boss npc object");
        yield return Node("entity.filter_by_health", "Filter By Health", "Entities", kind, "Passes the entity while its health fraction is below Threshold, else nil.",
            [In("entity", "Entity", PinType.Entity), In("threshold", "Threshold", PinType.Float, "0.5"), Out("entity_out", "", PinType.Entity)],
            keywords: "hp low");

        // Actions.
        yield return Node("mission.set_objective", "Set Objective", "Actions", kind, "Shows objective text (SDK set_objective).",
            [ExecIn, In("text", "Text", PinType.String), Then], keywords: "quest step hud");
        yield return Node("mission.spawn_squad", "Spawn Squad", "Actions", kind, "Spawns a squad by name (SDK spawn_squad).",
            [ExecIn, Then, Out("squad", "Squad", PinType.Entity)], [Prop("name", "Squad", PropertyKind.Text)], "combat wave enemies");
        yield return Node("mission.log", "Log", "Actions", kind, "Writes to the activity host log.",
            [ExecIn, In("text", "Text", PinType.String), Then], keywords: "print debug");
        yield return Node("mission.call", "Call SDK Function", "Actions", kind,
            "Calls context:<function>(args...) when this build has it; Result is its first return value.",
            [ExecIn, In("a1", "Arg 1", PinType.Any), In("a2", "Arg 2", PinType.Any), In("a3", "Arg 3", PinType.Any), Then,
             Out("result", "Result", PinType.Any)], [Prop("function", "Function", PropertyKind.Text, "")], "sdk api invoke");
        yield return Node("mission.lua", "Lua", "Actions", kind, "Raw Lua run in the handler (context, state and event are in scope).",
            [ExecIn, Then], [Prop("code", "Code", PropertyKind.Code, "-- lua")], "script snippet raw custom");
    }
}
