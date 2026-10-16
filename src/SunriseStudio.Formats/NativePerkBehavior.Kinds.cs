namespace SunriseStudio.Formats;

/** Node kind tables of the native perk behavior graph (always/timer and other class ids). */
public static partial class NativePerkBehavior
{
    private const uint AlwaysClass = 0x80803E03, TimerClass = 0x80803DCD;
    private const uint KillClass = 0x80803DE7, DamageClass = 0x80802F5F, UnequipClass = 0x80803DDB;
    private const uint StateClass = 0x80803DCE, GatedClass = 0x80803DCC, EventClass = 0x80803DEB;
    private const uint CounterClass = 0x80803E30, CounterRowClass = 0x80803E32;
    private const uint PropertyClass = 0x808029ED, AmmoDropClass = 0x80803E35;
    private const uint SpawnClass = 0x80803E45, DrivenSpawnClass = 0x80803E44, EnergyClass = 0x80803E4D;
    private const uint DamageScaleClass = 0x80802F16, CounterEffectClass = 0x80803E2F, TransmatClass = 0x80803E2E;
    // Damage multiplier bonus children: constant pair, input expression, owner value.
    private const uint BonusClass = 0x80802F1A, BonusExpressionClass = 0x80802F18, BonusOwnerClass = 0x80802F19;
    private const uint DamageRowClass = 0x80803E22;
    private const uint CodeClass = 0x80800009, ConstantClass = 0x80800090, StringClass = 0x80800065;
    // Every installed damage multiplier names this label table in both label references.
    private const uint LabelGlobalsTag = PerkLabelTable.Tag;
    private const string LabelGlobalsPath = "content/common/native/sandbox/label_globals.label_globals.tft";
    private const uint EmptyHash = InvestmentItemClone.EmptyStringHash, NoTag = uint.MaxValue;
    private const string StatePrefix = "state-";
    private static readonly byte[] ConstantCode = [0x34, 0, 0x3E, 0], PassCode = [0x3C, 0, 0x3E, 0];

    // Groups holds op 31 group rows, Group a group's members, Op32 an extend-timers effect's conditions.
    private enum Role { Top, Row, Inner, Groups, Group, Op32 }
    private enum Width { U8, I8, U32, F32, I32 }
    private sealed record Field(string Key, int Offset, Width Width);

    /** Object property names that installed while-state conditions read; each is the FNV-1 hash. */
    public static IReadOnlyList<PerkBehaviorChoice> StateProperties { get; } =
    [
        new(EmptyHash, "None"),
        .. new[]
        {
            "super_active", "is_thermal", "is_arc", "is_void", "reloading", "nearby_enemy_count", "melee_energy",
            "equipped_item_magazine_fraction", "is_guarding_with_sword", "melee_overcharge_state"
        }.Select(name => new PerkBehaviorChoice(InvestmentItemClone.Fnv1(name), name))
    ];

    /** Scalar inputs at fixed payload offsets, keyed by node shape; row fields are relative to the row. */
    private static readonly Dictionary<string, Field[]> Fields = new(StringComparer.Ordinal)
    {
        ["event"] = [new("event", 8, Width.U32)],
        ["unequip"] = [new("this_weapon", 8, Width.U8), new("slots", 9, Width.U8)],
        ["kill"] = [new("slots", 320, Width.U8), new("this_weapon", 321, Width.U8), new("min_share", 336, Width.F32)],
        ["damage"] = [new("slots", 152, Width.U8), new("this_weapon", 153, Width.U8), new("min_share", 160, Width.F32),
            new("positive_damage", 164, Width.U8)],
        ["while-state"] = [new("property", 212, Width.U32), new("minimum", 216, Width.F32),
            new("maximum", 220, Width.F32), new("hold", 224, Width.F32), new("invert", 248, Width.U8)],
        ["state"] = [new("property", 212, Width.U32), new("minimum", 216, Width.F32),
            new("maximum", 220, Width.F32), new("invert", 248, Width.U8)],
        ["counter"] = [new("threshold", 32, Width.F32), new("reset", 36, Width.F32),
            new("minimum", 40, Width.F32), new("maximum", 44, Width.F32)],
        ["row"] = [new("row_mode", 8, Width.U8), new("row_value", 12, Width.F32), new("row_window", 16, Width.F32)],
        ["spawn"] = [new("target", 2, Width.U8), new("definition", 16, Width.U32), new("keep", 24, Width.U32),
            new("removal", 28, Width.U32), new("counter_parameter", 48, Width.U32)],
        ["driven-spawn"] = [new("target", 2, Width.U8), new("definition", 16, Width.U32),
            new("input", 80, Width.I8), new("normalize", 81, Width.U8)],
        ["ability-energy"] = [new("slot", 2, Width.U8), new("gate", 3, Width.U8), new("amount", 8, Width.F32),
            new("cap", 12, Width.F32), new("input", 72, Width.I8)],
        ["damage-multiplier"] = [new("elements", 152, Width.U32), new("any_element", 176, Width.U8),
            new("this_weapon", 184, Width.U8)],
        ["perk-counter"] = [new("mode", 2, Width.U8), new("value", 4, Width.F32)],
        ["transmat"] = [new("key", 4, Width.U32)]
    };

    private static string Shape(string primitive) =>
        primitive.StartsWith(StatePrefix, StringComparison.Ordinal) ? "state" : primitive;

    private static PerkBehaviorNode Blank(string primitive, PerkBehaviorPrimitive spec) => new()
    {
        Primitive = primitive, Title = spec.Name, Kind = spec.Kind, NativeClass = spec.NativeClass,
        Editable = true, Description = spec.Description, Properties = Inputs(Shape(primitive), spec.Kind),
        LabelFilters = EmptyFilters(spec.NativeClass)
    };

    /** Inputs a new node exposes; installed nodes may add read-only details. */
    private static List<PerkBehaviorProperty> Inputs(string shape, PerkBehaviorNodeKind kind)
    {
        var rows = new List<PerkBehaviorProperty>();
        if (kind == PerkBehaviorNodeKind.Condition) rows.Add(Property("probability", "Probability", 1, 0, 1));
        switch (shape)
        {
            case "timer":
                rows.Add(Property("seconds", "Delay (seconds)", 3, 0, 3600));
                break;
            case "property":
                rows.Add(Choices(Property("target", "Target", 0, 0, 1, PerkBehaviorValueKind.Integer,
                    "0 selects the object carrying this perk. 1 selects its owner."), (0, "This object"), (1, "Owner")));
                rows.Add(Property("hash", "Property hash", 0, 1, uint.MaxValue - 1,
                    PerkBehaviorValueKind.Hash, "Select an authored property read by the weapon or its owner."));
                rows.Add(Property("value", "Value", 1, -1000000, 1000000));
                rows.Add(Choices(Property("operation", "Operation", 0, 0, 3, PerkBehaviorValueKind.Integer,
                    "0 sets the value. 1 adds it. 2 subtracts it. 3 multiplies it."),
                    (0, "Set"), (1, "Add"), (2, "Subtract"), (3, "Multiply")));
                rows.Add(Property("restore", "Restore on deactivation", 1, 0, 1, PerkBehaviorValueKind.Boolean));
                break;
            case "ammo-drop":
                foreach (string category in new[] { "primary", "special", "heavy" })
                {
                    string label = char.ToUpperInvariant(category[0]) + category[1..];
                    rows.Add(Property(category + "_interval", label + " interval reduction", 0, 0, 1,
                        description: "The native interval is multiplied by one minus this value."));
                    rows.Add(Property(category + "_chance", label + " chance scale bonus",
                        category == "primary" ? 0.25 : 0, 0, 4,
                        description: "The native chance is multiplied by one plus this value."));
                    rows.Add(Property(category + "_repeat", label + " repeat chance", 0, 0, 1,
                        description: "The native drop loop uses this chance for another allowed drop."));
                }
                break;
            case "event":
                rows.Add(Property("event", "Event hash", EmptyHash, 0, uint.MaxValue, PerkBehaviorValueKind.Hash,
                    "Pick a hash an installed perk uses. Which game event each hash is, is not known."));
                break;
            case "unequip":
                rows.Add(Property("this_weapon", "Only this weapon", 1, 0, 1, PerkBehaviorValueKind.Boolean,
                    "Passes only for the item carrying this perk."));
                rows.Add(Property("slots", "Slot mask", 0, 0, 15, PerkBehaviorValueKind.Integer,
                    "One bit per slot, 1 << slot. 0 accepts any slot."));
                break;
            case "kill":
            case "damage":
                rows.Add(Property("this_weapon", "Only this weapon", 1, 0, 1, PerkBehaviorValueKind.Boolean,
                    "Passes only for damage from the weapon carrying this perk."));
                rows.Add(Property("slots", "Weapon slot mask", 0, 0, 255, PerkBehaviorValueKind.Integer,
                    "One bit per weapon slot. 0 accepts any slot. Slot meanings are assumed."));
                rows.Add(Property("min_share", "Minimum damage share", 0, 0, 1,
                    description: "The owner's share of the damage the target took, from 0 to 1."));
                if (shape == "damage")
                    rows.Add(Property("positive_damage", "Damage above zero", 1, 0, 1, PerkBehaviorValueKind.Boolean,
                        "Passes only when the hit dealt damage."));
                break;
            case "while-state":
            case "state":
                rows.Add(new PerkBehaviorProperty
                {
                    Key = "property", Label = "Object property", Value = EmptyHash, Minimum = 0, Maximum = uint.MaxValue,
                    Kind = PerkBehaviorValueKind.Hash, Choices = [.. StateProperties],
                    Description = "A property of the object carrying the perk. None skips the range test."
                });
                rows.Add(Property("minimum", "Minimum", 1, -1000000, 1000000));
                rows.Add(Property("maximum", "Maximum", 1, -1000000, 1000000));
                if (shape == "while-state")
                    rows.Add(Property("hold", "Hold (seconds)", 0, 0, 3600,
                        description: "The test must stay true this long before the condition passes."));
                rows.Add(Property("invert", "Invert", 0, 0, 1, PerkBehaviorValueKind.Boolean,
                    "Passes when the test fails instead."));
                rows.Add(Property("this_weapon_active", "Only while this weapon is active", 0, 0, 1,
                    PerkBehaviorValueKind.Boolean));
                break;
            case "counter":
                rows.Add(Property("threshold", "Threshold", 3, -1000000, 1000000,
                    description: "Passes when the counter is at or above this value."));
                rows.Add(Property("reset", "Reset level", -1, -1, 1000000,
                    description: "The counter returns to 0 when it reaches this value. -1 turns it off."));
                rows.Add(Property("minimum", "Minimum", -9998, -1000000, 1000000));
                rows.Add(Property("maximum", "Maximum", 100, -1000000, 1000000));
                break;
            case "spawn":
            case "driven-spawn":
                rows.Add(Choices(Property("target", "Target", 0, 0, 3, PerkBehaviorValueKind.Integer,
                    "Event objects exist only when an event activated the perk."),
                    (0, "This object"), (1, "Owner"), (2, "Event object 1"), (3, "Event object 2")));
                rows.Add(Property("definition", "Object definition", 0, 0, uint.MaxValue, PerkBehaviorValueKind.Tag,
                    "An installed object definition (class 80809C0F). It decides what the effect does."));
                if (shape == "spawn")
                {
                    rows.Add(Property("keep", "Keep on removal (hash)", EmptyHash, 0, uint.MaxValue, PerkBehaviorValueKind.Hash,
                        "Empty (0x811C9DC5) ends the object when the perk ends. Any other value keeps it."));
                    rows.Add(Property("removal", "Removal message hash", EmptyHash, 0, uint.MaxValue, PerkBehaviorValueKind.Hash,
                        "Sent to the object when the perk ends. Empty sends nothing."));
                    rows.Add(Property("counter_parameter", "Counter parameter hash", EmptyHash, 0, uint.MaxValue,
                        PerkBehaviorValueKind.Hash, "Passes the perk counter to the object under this name. Empty passes nothing."));
                }
                else
                {
                    rows.AddRange(ExpressionInputs(1));
                    rows.Add(Property("normalize", "Divide by magazine size", 0, 0, 1, PerkBehaviorValueKind.Boolean,
                        "Used by the magazine inputs."));
                }
                break;
            case "ability-energy":
                rows.Add(Choices(Property("slot", "Ability", 0, 0, 7, PerkBehaviorValueKind.Integer,
                    "Slot meanings are assumed from perk names."),
                    (0, "Grenade"), (1, "Super"), (2, "Melee"), (7, "Class ability")));
                rows.Add(Choices(Property("gate", "Gate", 0, 0, 2, PerkBehaviorValueKind.Integer,
                    "Gate meanings are assumed."),
                    (0, "Always"), (1, "Only while the ability is not in use"), (2, "Only while the ability is in use")));
                rows.Add(Property("amount", "Amount", 0.1, -10, 10,
                    description: "Fraction of a full charge. Negative values drain."));
                rows.Add(Property("cap", "Cap", -1, -1, 10,
                    description: "The grant stops at this charge level. -1 turns the cap off."));
                rows.AddRange(ExpressionInputs(0));
                break;
            case "damage-multiplier":
                rows.Add(Property("bonus", "Bonus", 0.1, -10, 10,
                    description: "Damage is multiplied by one plus this value."));
                rows.Add(Property("player_bonus", "Bonus against players", 0, -10, 10,
                    description: "Used instead of Bonus when the target is a player."));
                rows.Add(Property("elements", "Element mask", 0, 0, uint.MaxValue, PerkBehaviorValueKind.Integer,
                    "2 Solar, 4 Arc, 8 Void (assumed). Used when Any element is off."));
                rows.Add(Property("any_element", "Any element", 1, 0, 1, PerkBehaviorValueKind.Boolean));
                rows.Add(Property("this_weapon", "Only this weapon", 1, 0, 1, PerkBehaviorValueKind.Boolean,
                    "Applies only to damage from the weapon carrying this perk."));
                break;
            case "perk-counter":
                rows.Add(Choices(Property("mode", "Mode", 1, 0, 1, PerkBehaviorValueKind.Integer), (0, "Add"), (1, "Set")));
                rows.Add(Property("value", "Value", 1, -1000000, 1000000));
                break;
            case "transmat":
                rows.Add(Property("key", "Effect key", 0, 0, uint.MaxValue, PerkBehaviorValueKind.Hash,
                    "A key from the installed transmat effect table."));
                break;
            case GroupKey:
                rows.Clear();
                rows.Add(Property("window", "Window (seconds)", 0, 0, 3600,
                    description: "A pass keeps the group passing this long. 0 needs a pass on the same event."));
                break;
            default:
                if (SpecOf(shape) is { } spec)
                    rows.AddRange(spec.Inputs.Select(input => new PerkBehaviorProperty
                    {
                        Key = input.Key, Label = input.Label, Value = input.Value, Minimum = input.Minimum,
                        Maximum = input.Maximum, Kind = input.Kind, Description = input.Description, Picker = input.Picker,
                        Choices = [.. (input.Choices ?? []).Select(choice => new PerkBehaviorChoice(choice.Value, choice.Label))]
                    }));
                break;
        }
        foreach (var row in rows)
            row.Picker = (shape, row.Key) switch
            {
                ("event", "event") => "event",
                ("spawn" or "driven-spawn", "definition") => "definitions",
                ("transmat", "key") => "transmat",
                _ => row.Picker
            };
        return rows;
    }

    private static List<PerkBehaviorProperty> ExpressionInputs(int form) =>
    [
        Choices(Property("expression", "Expression", form, 0, 1, PerkBehaviorValueKind.Integer,
            "Constant ignores the input. Pass through uses the input value."), (0, "Constant"), (1, "Pass input through")),
        Property("constant", "Constant", 1, -1000000, 1000000, description: "Used when Expression is Constant."),
        Choices(Property("input", "Input", form == 0 ? -1 : 0, -1, 12, PerkBehaviorValueKind.Integer,
            "Meanings other than the perk counter are assumed."),
            (-1, "None"), (0, "Perk counter"), (1, "Missing rounds"), (2, "Rounds in magazine"), (11, "Target value"))
    ];

    private static List<PerkBehaviorProperty> RowInputs() =>
    [
        Choices(Property("row_mode", "Row: on pass", 0, 0, 2, PerkBehaviorValueKind.Integer),
            (0, "Add"), (1, "Set"), (2, "Multiply")),
        Property("row_value", "Row: value", 1, -1000000, 1000000),
        Property("row_window", "Row: window (seconds)", 0, 0, 3600,
            description: "Each pass restarts the window. When it runs out the counter resets (assumed). 0 turns it off.")
    ];

    /** Checks a new node's inputs against the native rules the property ranges cannot express. */
    private static IEnumerable<string> Semantics(PerkBehaviorNode node)
    {
        double Get(string key) => node.Properties.FirstOrDefault(row => row.Key == key)?.Value ?? 0;
        switch (Shape(node.Primitive))
        {
            case "spawn" or "driven-spawn" when Get("definition") is 0 or NoTag:
                yield return $"{node.Title}: choose an object definition.";
                break;
            case "event" when Get("event") is 0 or EmptyHash:
                yield return $"{node.Title}: choose an event hash.";
                break;
            case "transmat" when Get("key") is 0 or EmptyHash:
                yield return $"{node.Title}: choose an effect key.";
                break;
            case "damage-multiplier" when Get("any_element") == 0 && Get("elements") == 0:
                yield return $"{node.Title}: choose an element or turn on Any element.";
                break;
            case "counter" when Get("minimum") > Get("maximum"):
                yield return $"{node.Title}: the minimum is above the maximum.";
                break;
        }
        if (SpecOf(node.Primitive) is { } kind)
            foreach (var input in kind.Inputs.Where(input => input.Required))
                if (Get(input.Key) is 0 or EmptyHash or NoTag) yield return $"{node.Title}: choose {input.Label}.";
        if (node.Properties.Any(row => row.Key == "any_type") && Get("any_type") == 0 && Get("types") == 0)
            yield return $"{node.Title}: choose a damage type or turn on Any damage type.";
        int children = Shape(node.Primitive) switch
        {
            "counter" or "all-groups" or GroupKey or "extend-timers" => -1, "state" => 1, _ => 0
        };
        if (children == -1 && node.Children.Count == 0) yield return $"{node.Title}: add at least one row.";
        if (children >= 0 && node.Children.Count != children) yield return $"{node.Title} has the wrong number of inner conditions.";
        // Group timers are looked up by group index, so timed groups come first.
        if (node.Primitive == "all-groups" && node.Children.SkipWhile(group => Window(group) > 0).Any(group => Window(group) > 0))
            yield return $"{node.Title}: put groups with a window first.";
        static double Window(PerkBehaviorNode group) => group.Properties.FirstOrDefault(row => row.Key == "window")?.Value ?? 0;
    }

    private static PerkBehaviorProperty Choices(PerkBehaviorProperty property, params (double Value, string Label)[] choices)
    {
        property.Choices = [.. choices.Select(choice => new PerkBehaviorChoice(choice.Value, choice.Label))];
        return property;
    }
}
