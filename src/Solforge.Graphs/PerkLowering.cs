/**
 * File: Lowers a custom perk definition (rules from the weapon graph compiler) to a native sandbox perk program plan,
 * plus JSON for the editor to pack (see Editor/Creation/CustomPerkAuthoring.cs).
 */
using System.Globalization;
using System.Text.Json.Nodes;

namespace Solforge.Graphs;

/**
 * One node of a native sandbox perk program, named by its NativePerkBehavior primitive key ("kill", "state-kill",
 * "damage-multiplier"...). Values are the node's input values by key.
 */
public sealed record NativePerkStep(string Primitive, IReadOnlyDictionary<string, double> Values)
{
    /** Inputs of a state-gated condition's inner event (the "kill" inside "state-kill"). */
    public IReadOnlyDictionary<string, double> InnerValues { get; init; } = new Dictionary<string, double>();
    /** Object property a state-gated condition tests, by FNV-1 name (equipped_item_magazine_fraction). */
    public string? StateProperty { get; init; }
    /** Label filter key ("source", "victim") to label names, any of which must be on the damage or victim. */
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AnyLabels { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

/** A custom perk lowered to the three native phases. No steps at all means a stats-only (passive) perk. */
public sealed record NativePerkPlan(IReadOnlyList<NativePerkStep> Activation, IReadOnlyList<NativePerkStep> Effects,
    IReadOnlyList<NativePerkStep> Deactivation, IReadOnlyList<GraphDiagnostic> Diagnostics)
{
    /** A plan with no steps or stats. */
    public static NativePerkPlan Empty { get; } = new([], [], [], []);
    public bool Ok => Diagnostics.All(item => item.Severity != DiagnosticSeverity.Error);
    /** Stats only: the plug carries investment stats and no sandbox perk program. */
    public bool IsPassive => Activation.Count == 0 && Effects.Count == 0;
}

/** A custom perk to pack (Studio.md Phase 27): display text, icon, plug stats, the weapon socket and its rules. */
public sealed record CustomPerkDefinition(string Name, string Description, string Icon, int Socket,
    IReadOnlyList<(string Stat, int Value)> Stats, IReadOnlyList<PerkRule> Rules, string NodeId)
{
    /** The native program the rules lower to (PerkLowering.Lower). */
    public NativePerkPlan Native { get; init; } = NativePerkPlan.Empty;
}

/**
 * Lowers custom perk rules to the native sandbox perk program the game client runs (Studio.md Phase 27).
 *
 * How a custom perk reaches the game: Solforge registers a new sandbox perk (definition, display and runtime-name
 * rows plus a compiled perk program tag) and a plug item that carries it and its stats, and puts that plug in a
 * weapon socket. The server reads plug sandbox perks and stats from build data and sends them in the character
 * record (the per-weapon perk bank and stat table); the client runs the program. The server never sees hits,
 * reloads or precision, so everything a perk does must be a native program; what no installed program can
 * express is a compile error here rather than a perk that silently does something else.
 */
public static class PerkLowering
{
    public const string MagazineProperty = "equipped_item_magazine_fraction";
    public const string PrecisionLabel = "precision", PlayerLabel = "player";
    /** Deactivation delay for perks with only instant effects, so the perk re-arms for the next event. */
    public const double RearmSeconds = 0.1;
    /** Largest damage scalar packed (x5); anything above is treated as a mistake. */
    public const double MaxScalar = 5;
    public const int MaxOrbs = 10;

    /** Turns the perk rules into native program steps; unsupported combinations become Error diagnostics. */
    public static NativePerkPlan Lower(CustomPerkDefinition perk)
    {
        var diagnostics = new List<GraphDiagnostic>();
        void Error(string message) => diagnostics.Add(new(DiagnosticSeverity.Error, $"Custom perk '{perk.Name}': {message}", perk.NodeId));
        void Warn(string message) => diagnostics.Add(new(DiagnosticSeverity.Warning, $"Custom perk '{perk.Name}': {message}", perk.NodeId));

        if (perk.Name.Length is 0 or > 200) Error("name it (1 to 200 characters).");
        if (perk.Description.Length > 4000) Error("the description is over 4000 characters.");
        if (perk.Socket < -1) Error("Socket is a weapon socket index from get_item, or -1 for the trait socket.");
        foreach (var (stat, value) in perk.Stats)
            if (value < -1000 || value > 1000) Error($"stat {stat} {value} is outside -1000..1000.");
        if (perk.Rules.Count == 0)
        {
            if (perk.Stats.Count == 0) Error("link it to an event (On Kill, On Reload...) or give it a stat.");
            return new([], [], [], diagnostics);
        }

        // One native program has one effect list: every event of a perk must run the same actions.
        string Signature(PerkRule rule) => string.Join("|", rule.Actions.Select(step =>
            step.Type + "(" + string.Join(",", step.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)) + ")"));
        if (perk.Rules.Select(Signature).Distinct(StringComparer.Ordinal).Count() > 1)
            Error("its events run different actions; a native perk has one effect list. Use one Custom Perk per action list.");

        var activation = new List<NativePerkStep>();
        foreach (var rule in perk.Rules)
            if (Activation(rule, Error) is { } step && !activation.Any(existing => Same(existing, step))) activation.Add(step);

        var effects = new List<NativePerkStep>();
        double duration = 0;
        foreach (var action in perk.Rules[0].Actions)
            switch (action.Type)
            {
                case "add_damage_scalar":
                {
                    double scalar = Number(action, "scalar", 1.2), seconds = Number(action, "duration", 5), stacks = Number(action, "stacks", 1);
                    if (!(scalar > 0) || scalar > MaxScalar) Error($"Add Damage Scalar {scalar} must be above 0 and at most {MaxScalar}.");
                    if (!(seconds >= RearmSeconds) || seconds > 3600) Error($"Add Damage Scalar duration {seconds} must be {RearmSeconds} to 3600 seconds.");
                    if (stacks > 1) Warn("Max stacks is packed as one buff that the next event refreshes; native stacking is not lowered.");
                    effects.Add(new("damage-multiplier", new Dictionary<string, double>
                        { ["bonus"] = scalar - 1, ["player_bonus"] = scalar - 1, ["any_element"] = 1, ["this_weapon"] = 1 }));
                    duration = Math.Max(duration, seconds);
                    break;
                }
                case "refill_magazine":
                {
                    double percent = Number(action, "percent", 100);
                    if (!(percent > 0) || percent > 100) Error($"Refill Magazine {percent}% must be above 0 and at most 100.");
                    effects.Add(new("refill", new Dictionary<string, double> { ["this_fraction"] = percent / 100, ["magazine_base"] = 1 }));
                    break;
                }
                case "grant_super_energy":
                {
                    double percent = Number(action, "percent", 5);
                    if (!(percent > 0) || percent > 100) Error($"Grant Super Energy {percent}% must be above 0 and at most 100.");
                    effects.Add(new("ability-energy", new Dictionary<string, double> { ["slot"] = 1, ["gate"] = 1, ["amount"] = percent / 100 }));
                    break;
                }
                case "spawn_orb":
                {
                    double count = Math.Round(Number(action, "count", 1));
                    if (count < 1 || count > MaxOrbs) Error($"Spawn Orb count {count} must be 1 to {MaxOrbs}.");
                    effects.Add(new("orbs", new Dictionary<string, double> { ["count"] = count }));
                    break;
                }
                case "explode":
                    Error("Spawn Explosion needs an installed explosion object definition, which this node cannot choose yet. Remove it or build the perk in the Perk Behavior editor (spawn-once).");
                    break;
                case "heal":
                    Error("Heal has no installed perk effect that restores health, so it cannot be packed. Remove it.");
                    break;
                default:
                    Error($"action {action.Type} has no native lowering.");
                    break;
            }
        if (effects.Count == 0 && perk.Rules[0].Actions.Count == 0) Error("its event chain has no actions.");

        var deactivation = new List<NativePerkStep>
            { new("timer", new Dictionary<string, double> { ["seconds"] = duration > 0 ? duration : RearmSeconds }) };
        if (diagnostics.All(item => item.Severity != DiagnosticSeverity.Error))
            diagnostics.Add(new(DiagnosticSeverity.Info,
                $"Custom perk '{perk.Name}' packs as a sandbox perk program: {string.Join(", ", activation.Select(step => step.Primitive))} -> "
                + $"{string.Join(", ", effects.Select(step => step.Primitive))}, ends after {deactivation[0].Values["seconds"]}s. "
                + "Only timers are proven in game so far; test it in game.", perk.NodeId));
        return new(activation, effects, deactivation, diagnostics);
    }

    /** The event, gated by a magazine range or filtered by labels when its conditions ask for that. */
    private static NativePerkStep? Activation(PerkRule rule, Action<string> error)
    {
        string inner;
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var labels = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        void Label(string key, string label) =>
            labels[key] = labels.TryGetValue(key, out var known) ? known.Append(label).Distinct().ToList() : new List<string> { label };
        switch (rule.Event)
        {
            case "on_hit": inner = "damage"; values["this_weapon"] = 1; values["positive_damage"] = 1; break;
            case "on_precision_hit": inner = "damage"; values["this_weapon"] = 1; values["positive_damage"] = 1; Label("source", PrecisionLabel); break;
            case "on_kill": inner = "kill"; values["this_weapon"] = 1; break;
            case "on_precision_kill": inner = "kill"; values["this_weapon"] = 1; Label("source", PrecisionLabel); break;
            case "on_reload": inner = "reload"; values["this_weapon"] = 1; break;
            case "on_ability_use": inner = "ability-used"; values["abilities"] = 255; break;
            default: error($"event {rule.Event} has no native condition."); return null;
        }
        (double Minimum, double Maximum)? range = null;
        foreach (var condition in rule.Conditions)
            switch (condition.Type)
            {
                case "magazine_percentage":
                    double percent = Number(condition, "percent", 50);
                    if (!(percent >= 0 && percent <= 100)) error($"Magazine Percentage {percent} must be 0 to 100.");
                    if (range is not null) error("use one Magazine Percentage per event.");
                    range = (0, percent / 100);
                    break;
                case "target_type":
                    string target = condition.Values.GetValueOrDefault("target", "Any");
                    if (target == "Any") break;
                    if (target == "Guardian" && inner == "kill") { Label("victim", PlayerLabel); break; }
                    error($"Target Type {target} has no label the native {inner} condition checks; only Any, and Guardian on kills, can be packed.");
                    break;
                case "health_below":
                    error("Health Below cannot be packed: no installed perk program reads the wielder's health. Remove it.");
                    break;
                case "has_buff":
                    error("Has Buff cannot be packed: native perks name no buffs. Remove it.");
                    break;
                default:
                    error($"condition {condition.Type} has no native lowering.");
                    break;
            }
        if (range is not { } gate) return new(inner, values) { AnyLabels = labels };
        return new("state-" + inner, new Dictionary<string, double> { ["minimum"] = gate.Minimum, ["maximum"] = gate.Maximum })
            { InnerValues = values, StateProperty = MagazineProperty, AnyLabels = labels };
    }

    /** Whether two native steps are identical (same primitive, state property and values). */
    private static bool Same(NativePerkStep left, NativePerkStep right) =>
        left.Primitive == right.Primitive && left.StateProperty == right.StateProperty
        && left.Values.OrderBy(pair => pair.Key).SequenceEqual(right.Values.OrderBy(pair => pair.Key))
        && left.InnerValues.OrderBy(pair => pair.Key).SequenceEqual(right.InnerValues.OrderBy(pair => pair.Key))
        && left.AnyLabels.Count == right.AnyLabels.Count
        && left.AnyLabels.All(pair => right.AnyLabels.TryGetValue(pair.Key, out var other) && pair.Value.SequenceEqual(other));

    /** Reads a numeric step value, with a fallback. */
    private static double Number(PerkStep step, string key, double fallback) =>
        step.Values.TryGetValue(key, out string? text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value : fallback;

    /** JSON form of a custom perk definition. */
    public static JsonObject ToJson(CustomPerkDefinition perk) => new()
    {
        ["name"] = perk.Name, ["description"] = perk.Description, ["icon"] = perk.Icon, ["socket"] = perk.Socket, ["node"] = perk.NodeId,
        ["stats"] = new JsonArray(perk.Stats.Select(stat => (JsonNode)new JsonObject { ["stat"] = stat.Stat, ["value"] = stat.Value }).ToArray()),
        ["ok"] = perk.Native.Ok, ["passive"] = perk.Native.IsPassive,
        ["activation"] = Steps(perk.Native.Activation), ["effects"] = Steps(perk.Native.Effects), ["deactivation"] = Steps(perk.Native.Deactivation)
    };

    /** JSON form of native steps, values rounded to six places. */
    private static JsonArray Steps(IReadOnlyList<NativePerkStep> steps) => new(steps.Select(step =>
    {
        var values = new JsonObject();
        foreach (var (key, value) in step.Values) values[key] = Math.Round(value, 6);
        var row = new JsonObject { ["primitive"] = step.Primitive, ["values"] = values };
        if (step.StateProperty is { } property) row["state"] = property;
        if (step.InnerValues.Count > 0)
        {
            var inner = new JsonObject();
            foreach (var (key, value) in step.InnerValues) inner[key] = Math.Round(value, 6);
            row["inner"] = inner;
        }
        if (step.AnyLabels.Count > 0)
        {
            var labels = new JsonObject();
            foreach (var (key, names) in step.AnyLabels) labels[key] = new JsonArray(names.Select(name => (JsonNode)name).ToArray());
            row["anyLabels"] = labels;
        }
        return (JsonNode)row;
    }).ToArray());
}
