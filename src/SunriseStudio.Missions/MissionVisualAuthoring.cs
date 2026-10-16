namespace SunriseStudio.Missions;

/** Scene is a scene's type-43 slot; AuthoredScene is its mission.Scene entry, the one context:scene takes. */
public enum MissionReferenceKind
{
    Directive, Navpoint, Volume, Trigger, Monitor, GhostLink, Object, Scene, State, Dialogue, DirectiveSensor, DialogueSensor,
    AuthoredScene, SceneInput, SceneOutput
}
/** Node recipes that visual authoring can add to a graph. */
public enum MissionNodeTemplate
{
    ActionStep, ReachTrigger, GhostScan, Interact, FlowObjective, FlowDialogue, FlowState, FlowArmTrigger, FlowCompleteMission, TriggerFact,
    FlowStartScene, FlowSceneInput, SceneOutputFact
}
/** One dialogue line of a node with its cue and filter. */
public sealed record MissionDialogueLine(LuaField Entry, MissionSdkReference? Cue, MissionSdkReference? Filter, bool Editable);

/** Typed operations emit installed helper contracts and preserve unrelated Lua source spans. */
public sealed partial class MissionVisualAuthoring(MissionGraphDocument graph, IReadOnlyList<MissionSdkReference> references)
{
    /** Values are the installed SDK's slot type identifiers. */
    private enum SlotKind { Object = 4, Monitor = 30, Trigger = 31, Scene = 43, Navpoint = 47, Dialogue = 53, Volume = 60, GhostLink = 65, Directive = 68 }
    private readonly Dictionary<MissionReferenceKind, IReadOnlyList<MissionSdkReference>> choices = [];
    public MissionScriptBindings Bindings { get; } = new(graph.Script);
    /** SDK references the user can pick for a kind of field. */
    public IReadOnlyList<MissionSdkReference> Choices(MissionReferenceKind kind)
    {
        if (!choices.TryGetValue(kind, out var result)) choices.Add(kind, result = references.Where(value => Accepts(value, kind)).ToArray());
        return result;
    }
    /** Resolves an expression to its SDK reference, expanding local bindings. */
    public MissionSdkReference? Resolve(string expression) => Index.Resolve(Bindings.Expand(expression), LuaScript.DecodeString(expression));
    private MissionSdkIndex Index => MissionSdkIndex.For(references);

    /** True when the entry is of the kind, judged by its table, its category or its slot type. */
    public static bool Accepts(MissionSdkReference item, MissionReferenceKind kind) => kind switch
    {
        MissionReferenceKind.Directive => item.Expression.StartsWith("mission.Directive.", StringComparison.Ordinal),
        MissionReferenceKind.State => item.Expression.StartsWith("mission.states.", StringComparison.Ordinal),
        MissionReferenceKind.Dialogue => item.Category == "dialogue",
        MissionReferenceKind.AuthoredScene => item.Expression.StartsWith("mission.Scene.", StringComparison.Ordinal),
        MissionReferenceKind.SceneInput => item.Category == "scene_inputs",
        MissionReferenceKind.SceneOutput => item.Category == "scene_outputs",
        _ => item.Expression.StartsWith("mission.Slot.", StringComparison.Ordinal) && item.SlotType == (kind switch
        {
            MissionReferenceKind.Navpoint => (int)SlotKind.Navpoint, MissionReferenceKind.Volume => (int)SlotKind.Volume,
            MissionReferenceKind.Trigger => (int)SlotKind.Trigger, MissionReferenceKind.Monitor => (int)SlotKind.Monitor,
            MissionReferenceKind.GhostLink => (int)SlotKind.GhostLink, MissionReferenceKind.Object => (int)SlotKind.Object,
            MissionReferenceKind.Scene => (int)SlotKind.Scene, MissionReferenceKind.DirectiveSensor => (int)SlotKind.Directive,
            MissionReferenceKind.DialogueSensor => (int)SlotKind.Dialogue,
            _ => -1
        })
    };

    /** Sets or clears a typed reference field; the field path fixes the kind it takes. */
    public MissionGraphDocument SetReference(string node, string path, MissionReferenceKind kind, MissionSdkReference? value)
    {
        var allowed = path switch
        {
            "directive" => MissionReferenceKind.Directive, "navpoint" => MissionReferenceKind.Navpoint,
            "waypoint" => MissionReferenceKind.Volume, "scene" => MissionReferenceKind.AuthoredScene, "ends.scene" => MissionReferenceKind.Scene,
            "ends.trigger" or "trigger" => MissionReferenceKind.Trigger, "ends.monitor" or "monitor" => MissionReferenceKind.Monitor,
            "ends.ghost_link" => MissionReferenceKind.GhostLink, "ends.interact" => MissionReferenceKind.Object,
            "state" => MissionReferenceKind.State, _ => throw new NotSupportedException("This field has no typed SDK editor.")
        };
        if (allowed != kind) throw new InvalidDataException("SDK reference kind does not match this field.");
        if (value is null) return graph.Field(node, path) is null ? graph : graph.RemoveField(node, path);
        Require(value, kind); return graph.SetField(node, path, Bindings.Reference(value));
    }

    /** Adds a dialogue line to a node. */
    public MissionGraphDocument AddDialogue(string node, MissionSdkReference cue, MissionSdkReference? filter = null)
    {
        Require(cue, MissionReferenceKind.Dialogue);
        if (filter is not null) Require(filter, MissionReferenceKind.Volume);
        ValidateSensor(node, cue);
        return graph.AppendValue(node, "lines", DialogueExpression(cue, filter));
    }

    /** Sets a campaign step's trigger and arms it in the chosen leg unless that leg already arms it. */
    public MissionGraphDocument SetTrigger(string node, MissionSdkReference trigger, string legKey)
    {
        Require(trigger, MissionReferenceKind.Trigger);
        var group = graph.GroupOf(node);
        var leg = graph.NodesIn(group, "legs").FirstOrDefault(value => value.Key == legKey);
        if (!group.IsCampaignSteps || leg is null)
            throw new InvalidDataException("Choose a leg from this campaign.");
        var next = graph.SetField(node, "ends.trigger", Bindings.Reference(trigger));
        bool armed = graph.Field(leg.Key, "arm")?.Table?.Fields.Any(value => Resolve(value.Value.Read(graph.Source))?.Identity == trigger.Identity) == true;
        return armed ? next : next.AppendValue(leg.Key, "arm", Bindings.Reference(trigger));
    }

    /** Replaces one dialogue line of a node. */
    public MissionGraphDocument SetDialogue(string node, int index, MissionSdkReference cue, MissionSdkReference? filter)
    {
        Require(cue, MissionReferenceKind.Dialogue); if (filter is not null) Require(filter, MissionReferenceKind.Volume);
        ValidateSensor(node, cue);
        if (!Dialogue(node)[index].Editable) throw new NotSupportedException("This dialogue entry is supplied by custom Lua.");
        return graph.SetListValue(node, "lines", index, DialogueExpression(cue, filter));
    }

    /** The step's dialogue lines, as tables or campaign.line calls; an editable line holds only a cue and an optional filter, both resolved. */
    public IReadOnlyList<MissionDialogueLine> Dialogue(string node)
    {
        if (graph.Field(node, "lines")?.Table is not { } lines) return [];
        return lines.Fields.Select(entry =>
        {
            string? cue = entry.Table?.Field("cue")?.Value.Read(graph.Source);
            string? filter = entry.Table?.Field("filter")?.Value.Read(graph.Source);
            bool known = entry.Table is not null && entry.Table.Fields.All(field => field.Name is "cue" or "filter");
            if (entry.Expression is LuaCall { Method: null, Arguments: { Form: LuaArgumentsForm.List, Values: var arguments } } call
                && Bindings.Expand(call.Target.Span.Read(graph.Source)) == "campaign.line")
            {
                known = arguments.Count is 1 or 2;
                if (known) { cue = arguments[0].Span.Read(graph.Source); filter = arguments.Count == 2 ? arguments[1].Span.Read(graph.Source) : null; }
            }
            var resolvedCue = cue is null ? null : Resolve(cue); var resolvedFilter = filter is null ? null : Resolve(filter);
            return new MissionDialogueLine(entry, resolvedCue, resolvedFilter,
                known && resolvedCue?.Category == "dialogue" && (filter is null || resolvedFilter is not null));
        }).ToArray();
    }

    /** Trigger templates arm their chosen leg in the same source transaction as the new step. */
    public MissionGraphDocument CreateNode(string groupKey, string id, MissionNodeTemplate template,
        MissionSdkReference? target = null, string? legKey = null, MissionSdkReference? sensor = null,
        MissionSdkReference? navpoint = null, MissionSdkReference? scene = null, IReadOnlyList<MissionSdkReference>? inputs = null)
    {
        var group = graph.Group(groupKey);
        var next = graph.AddNode(groupKey, id); string node = groupKey + "/" + id;
        if (group.IsCampaignSteps)
        {
            (string Path, MissionReferenceKind? Kind) end = template switch
            {
                MissionNodeTemplate.ReachTrigger => ("trigger", MissionReferenceKind.Trigger),
                MissionNodeTemplate.GhostScan => ("ghost_link", MissionReferenceKind.GhostLink),
                MissionNodeTemplate.Interact => ("interact", MissionReferenceKind.Object),
                MissionNodeTemplate.ActionStep => ("", null),
                _ => throw new InvalidOperationException("Choose a campaign step template.")
            };
            if (end.Kind is { } kind) next = next.SetField(node, "ends." + end.Path, RequiredReference(target, kind));
            if (template == MissionNodeTemplate.ReachTrigger)
            {
                next = new MissionVisualAuthoring(next, references).SetTrigger(node, target!, legKey ?? "");
            }
        }
        else if (group.IsFlowSteps)
        {
            next = next.SetField(node, "run", FlowActionSource(new(template, target, sensor, navpoint, scene, inputs)));
        }
        else if (group.IsFlowFacts && template is MissionNodeTemplate.TriggerFact or MissionNodeTemplate.SceneOutputFact)
        {
            // event.kind is an EventKind number, never a name.
            string trigger = template == MissionNodeTemplate.TriggerFact ? RequiredReference(target, MissionReferenceKind.Trigger) : "";
            string output = template == MissionNodeTemplate.SceneOutputFact ? RequiredReference(target, MissionReferenceKind.SceneOutput) : "";
            string slot = template == MissionNodeTemplate.SceneOutputFact ? RequiredReference(SceneSlot(target!.Scene)
                ?? throw new InvalidDataException("This scene has no slot in the installed SDK."), MissionReferenceKind.Scene) : "";
            (next, string kinds) = Import(next, "EventKind");
            string observe;
            if (template == MissionNodeTemplate.TriggerFact)
                observe = $"function(context, state, event) if event.kind == {kinds}.PLAYER_TRIGGER and event.slot ~= nil"
                    + $" and event.slot.id == context:slot({trigger}).id"
                    + " then event.slot:disarm_trigger() return true end return false end";
            else
                observe = $"function(context, state, event) return event.kind == {kinds}.SCENE_OUTPUT and event.slot ~= nil"
                    + $" and event.slot.id == context:slot({slot}).id and event.key == {output} end";
            next = next.SetField(node, "observe", observe);
        }
        else throw new InvalidOperationException("This group has no matching node template.");
        next.RequireValid(); return next;
    }

    /** Sets or clears the fact condition in a node field. */
    public MissionGraphDocument SetFactCondition(string node, string field, string? fact)
        => SetCondition(node, field, fact is null ? null : new MissionFactCondition(fact));

    /** Sets or clears a flow step's when or await condition; its facts must belong to the same flow. */
    public MissionGraphDocument SetCondition(string node, string field, MissionCondition? condition)
    {
        var group = graph.GroupOf(node);
        if (!group.IsFlowSteps || field is not ("when" or "await"))
            throw new InvalidOperationException("Fact conditions belong to flow steps.");
        if (condition is null) return graph.Field(node, field) is null ? graph : graph.RemoveField(node, field);
        var facts = graph.NodesIn(group, "facts").Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        if (MissionConditions.Facts(condition).Any(fact => !facts.Contains(fact)))
            throw new InvalidDataException("Choose facts from this flow.");
        var next = graph.SetField(node, field, MissionConditions.Write(condition));
        next.RequireValid(); return next;
    }

    private string DialogueExpression(MissionSdkReference cue, MissionSdkReference? filter) => "{ cue = " + Bindings.Reference(cue)
        + (filter is null ? "" : ", filter = " + Bindings.Reference(filter)) + " }";
    private void Require(MissionSdkReference value, MissionReferenceKind kind)
    { if (!Index.Contains(value) || !Accepts(value, kind)) throw new InvalidDataException("Choose a matching installed SDK reference."); }

    /** The script source for a chosen reference; a missing or wrong-kind choice throws. */
    private string RequiredReference(MissionSdkReference? value, MissionReferenceKind kind)
    {
        if (value is null) throw new InvalidDataException("Choose a " + Label(kind) + ".");
        Require(value, kind);
        return Bindings.Reference(value);
    }

    private void ValidateSensor(string node, MissionSdkReference cue)
    {
        var definition = graph.GroupOf(node).Definition!;
        string? sensor = definition.Field("dialogue_sensor")?.Value.Read(graph.Source);
        if (sensor is null || Bindings.Expand(sensor) != cue.Sensor)
            throw new InvalidDataException("Dialogue cue belongs to another sensor; choose this campaign's dialogue sensor.");
    }
}
