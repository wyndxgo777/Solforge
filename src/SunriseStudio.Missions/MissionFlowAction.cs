namespace SunriseStudio.Missions;

/** Scene is the mission.Scene entry; Inputs are the inputs a Start scene action sends after activation. */
public sealed record MissionFlowAction(MissionNodeTemplate Kind, MissionSdkReference? Target = null,
    MissionSdkReference? Sensor = null, MissionSdkReference? Navpoint = null, MissionSdkReference? Scene = null,
    IReadOnlyList<MissionSdkReference>? Inputs = null);

/** Partial: flow action editing for visual mission authoring. */
public sealed partial class MissionVisualAuthoring
{
    /** Only the exact supported callback forms become typed actions; other Lua remains untouched. */
    public MissionFlowAction? ReadFlowAction(string node)
    {
        var run = graph.Field(node, "run")?.Expression;
        if (run is null or LuaLiteral { Kind: LuaLiteralKind.Nil }) return new(MissionNodeTemplate.ActionStep);
        if (run is not LuaFunction { Parameters: ["context", "state"] } function) return null;
        if (graph.Script.Comments.Any(span => span.Start < function.Span.End && span.End > function.Span.Start)) return null;
        if (function.Body.Statements.Count == 0) return new(MissionNodeTemplate.ActionStep);
        var calls = new List<LuaCall>();
        foreach (var statement in function.Body.Statements)
        {
            if (statement is not LuaCallStatement { Expression: LuaCall call }) return null;
            calls.Add(call);
        }
        var action = ReadStartScene(calls) ?? (calls.Count == 1 ? ReadCall(calls[0]) : null);
        if (action is null) return null;
        try { _ = FlowActionSource(action); return action; }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException) { return null; }
    }

    /** One call of a single-call action, in the form FlowActionSource writes it. */
    private MissionFlowAction? ReadCall(LuaCall call)
    {
        string? slot = Handle(call.Target, "slot");
        switch (call.Method)
        {
            case "set_directive" when slot is not null && Fields(call, "directive", "navpoint") is { } objective
                && DottedPath(objective.GetValueOrDefault("directive")) is { } directive:
                if (!objective.TryGetValue("navpoint", out var navpoint))
                    return new(MissionNodeTemplate.FlowObjective, Resolve(directive), Resolve(slot));
                return Handle(navpoint, "slot") is { } nav
                    ? new(MissionNodeTemplate.FlowObjective, Resolve(directive), Resolve(slot), Resolve(nav)) : null;
            case "play_dialogue_cue" when slot is not null && DottedPath(Fields(call, "cue")?.GetValueOrDefault("cue")) is { } cue:
                return new(MissionNodeTemplate.FlowDialogue, Resolve(cue), Resolve(slot));
            case "fire_trigger" when slot is not null && call.Arguments is { Form: LuaArgumentsForm.List, Values.Count: 0 }:
                return new(MissionNodeTemplate.FlowArmTrigger, Resolve(slot));
            case "select_state" when Handle(call, "select_state") is { } state:
                return new(MissionNodeTemplate.FlowState, Resolve(state));
            case "complete_mission" when OnContext(call, "complete_mission") && Fields(call) is { Count: 0 }:
                return new(MissionNodeTemplate.FlowCompleteMission);
            case "send_event" when Handle(call.Target, "scene") is { } scene && DottedPath(Fields(call, "key")?.GetValueOrDefault("key")) is { } key:
                return Resolve(key) is { } input ? new(MissionNodeTemplate.FlowSceneInput, input, Scene: Resolve(scene)) : null;
            default:
                return null;
        }
    }

    /** context:scene(S):activate{} and then sends to the same scene; null for any other body. */
    private MissionFlowAction? ReadStartScene(IReadOnlyList<LuaCall> calls)
    {
        var activate = calls[0];
        if (activate.Method != "activate" || Handle(activate.Target, "scene") is not { } scene || Fields(activate) is not { Count: 0 }) return null;
        var inputs = new List<MissionSdkReference>();
        foreach (var send in calls.Skip(1))
        {
            if (send.Method != "send_event" || Handle(send.Target, "scene") is not { } again || Bindings.Expand(again) != Bindings.Expand(scene)
                || DottedPath(Fields(send, "key")?.GetValueOrDefault("key")) is not { } key || Resolve(key) is not { } input) return null;
            inputs.Add(input);
        }
        return new(MissionNodeTemplate.FlowStartScene, Scene: Resolve(scene), Inputs: inputs);
    }

    private static bool OnContext(LuaCall call, string method) => call.Method == method && call.Target is LuaName { Name: "context" };

    /** The path X of context:method(X); null for any other expression. */
    private static string? Handle(LuaExpression target, string method) =>
        target is LuaCall { Arguments: { Form: LuaArgumentsForm.List, Values: [var value] } } call && OnContext(call, method) ? DottedPath(value) : null;

    /** The fields of a call's table argument; null when it takes no table or the table has a key not in names. */
    private static Dictionary<string, LuaExpression>? Fields(LuaCall call, params string[] names)
    {
        if (call.Arguments.Table is not { } table) return null;
        var result = new Dictionary<string, LuaExpression>(StringComparer.Ordinal);
        foreach (var field in table.Fields)
            if (field.Key is not null || field.Name is not { } name || !names.Contains(name) || !result.TryAdd(name, field.Expression)) return null;
        return result;
    }

    /** A dotted path such as mission.Slot.X; null for a bare name, a string or any other expression. */
    private static string? DottedPath(LuaExpression? value) => value switch
    {
        LuaMember { Target: LuaName root } member => root.Name + "." + member.Name,
        LuaMember member => DottedPath(member.Target) is { } target ? target + "." + member.Name : null,
        _ => null
    };

    /** Rewrites the step's run callback from a typed action; custom Lua callbacks are refused. */
    public MissionGraphDocument SetFlowAction(string node, MissionFlowAction action)
    {
        if (!graph.GroupOf(node).IsFlowSteps)
            throw new InvalidOperationException("Typed flow actions belong to flow steps.");
        if (ReadFlowAction(node) is null) throw new NotSupportedException("This callback is custom Lua; edit it in Advanced Lua.");
        var next = graph.SetField(node, "run", FlowActionSource(action)); next.RequireValid(); return next;
    }

    /** The run callback for an action; a missing or wrong-kind reference throws. */
    private string FlowActionSource(MissionFlowAction action)
    {
        // A Send scene input action may name only its input; the input names its scene.
        var scene = action.Scene ?? (action.Kind == MissionNodeTemplate.FlowSceneInput && action.Target is { } input ? AuthoredScene(input.Scene) : null);
        string Send(MissionSdkReference? value)
        {
            string handle = RequiredReference(scene, MissionReferenceKind.AuthoredScene), key = RequiredReference(value, MissionReferenceKind.SceneInput);
            if (value!.Scene != SceneKey(scene!)) throw new InvalidDataException(value.Name + " is not an input of this scene.");
            return "context:scene(" + handle + "):send_event{key = " + key + "}";
        }
        string source = action.Kind switch
        {
            MissionNodeTemplate.ActionStep => "",
            MissionNodeTemplate.FlowObjective => "context:slot(" + RequiredReference(action.Sensor, MissionReferenceKind.DirectiveSensor)
                + "):set_directive{directive = " + RequiredReference(action.Target, MissionReferenceKind.Directive)
                + (action.Navpoint is null ? "" : ", navpoint = context:slot(" + RequiredReference(action.Navpoint, MissionReferenceKind.Navpoint) + ")") + "}",
            MissionNodeTemplate.FlowDialogue => "context:slot(" + RequiredReference(action.Sensor, MissionReferenceKind.DialogueSensor)
                + "):play_dialogue_cue{cue = " + RequiredReference(action.Target, MissionReferenceKind.Dialogue) + "}",
            MissionNodeTemplate.FlowState => "context:select_state(" + RequiredReference(action.Target, MissionReferenceKind.State) + ")",
            MissionNodeTemplate.FlowArmTrigger => "context:slot(" + RequiredReference(action.Target, MissionReferenceKind.Trigger) + "):fire_trigger()",
            MissionNodeTemplate.FlowCompleteMission => "context:complete_mission{}",
            // Inputs follow the activation, in list order.
            MissionNodeTemplate.FlowStartScene => string.Join(" ", (action.Inputs ?? []).Select(Send)
                .Prepend("context:scene(" + RequiredReference(scene, MissionReferenceKind.AuthoredScene) + "):activate{}")),
            MissionNodeTemplate.FlowSceneInput => Send(action.Target),
            _ => throw new InvalidOperationException("Choose a supported flow action.")
        };
        if (action.Kind == MissionNodeTemplate.FlowDialogue && action.Target!.Sensor != action.Sensor!.Expression)
            throw new InvalidDataException("Dialogue cue belongs to another sensor.");
        return "function(context, state) " + source + " end";
    }
}
