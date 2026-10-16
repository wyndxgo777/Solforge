namespace SunriseStudio.Formats;

/** Node spec table: the editable properties each supported native node class exposes. */
public static partial class NativePerkBehavior
{
    private const uint AllGroupsClass = 0x80803E04, GroupClass = 0x80803E06, ExtendTimersClass = 0x80803E3B;
    private const uint ResistClass = 0x80803E3C, LabelRowClass = 0x808094B3, TypeListClass = 0x80806B02;
    private const string GroupKey = "group";

    /** One scalar input at a fixed payload offset. Required inputs refuse 0 and the empty values. */
    private sealed record Input(string Key, string Label, int Offset, Width Width, double Value, double Minimum,
        double Maximum, PerkBehaviorValueKind Kind = PerkBehaviorValueKind.Float, string Description = "",
        (double Value, string Label)[]? Choices = null, string Picker = "", bool Required = false);

    /** A fixed byte value a new node writes; installed nodes that differ show a read-only note. */
    private sealed record Fixed(int Offset, Width Width, double Value);

    /**
     * A table-driven node kind. Handler names a payload part the generic code cannot write.
     * Ignore lists byte ranges (offset, length) left out of the "other settings" comparison.
     * LabelSet is the offset of a label set whose reference copies the installed label table, or -1.
     */
    private sealed record Spec(string Key, string Name, PerkBehaviorNodeKind Kind, uint Class, int Opcode, int Size,
        string Description, Input[] Inputs, Fixed[]? Defaults = null, (int Offset, int Length)[]? Ignore = null,
        string Handler = "", bool CanCreate = true, int LabelSet = -1);

    /** Built on first use, so it never depends on the order of static initializers. */
    private static class SpecTable
    {
        public static readonly Spec[] All = Build();
        public static readonly Dictionary<string, Spec> ByKey = All.ToDictionary(spec => spec.Key, StringComparer.Ordinal);
        public static readonly Dictionary<string, Field[]> Fields = All.ToDictionary(spec => spec.Key,
            spec => spec.Inputs.Where(input => input.Offset >= 0).Select(input => new Field(input.Key, input.Offset, input.Width))
                .ToArray(), StringComparer.Ordinal);
    }

    private static Spec? SpecOf(string key) => SpecTable.ByKey.GetValueOrDefault(key);

    /** Fixed-offset inputs of a node shape, from the hand-written table or the spec table. */
    private static Field[]? FieldsOf(string shape) =>
        Fields.TryGetValue(shape, out var fields) ? fields : SpecTable.Fields.GetValueOrDefault(shape);

    private static Spec? SpecOf(PerkBehaviorNodeKind kind, uint type, int opcode) =>
        SpecTable.All.FirstOrDefault(spec => spec.Kind == kind && spec.Class == type && spec.Opcode == opcode);

    // --- Shared input shapes.
    private static Input Flag(string key, string label, int offset, double value = 0, string description = "") =>
        new(key, label, offset, Width.U8, value, 0, 1, PerkBehaviorValueKind.Boolean, description);

    private static Input Mask(string key, string label, int offset, double value, double maximum, string description) =>
        new(key, label, offset, Width.U8, value, 0, maximum, PerkBehaviorValueKind.Integer, description);

    private static Input Number(string key, string label, int offset, double value, double minimum, double maximum,
        string description = "") => new(key, label, offset, Width.F32, value, minimum, maximum, Description: description);

    private static Input HashInput(string key, string label, int offset, string picker, string description,
        bool required = true, (double, string)[]? choices = null) =>
        new(key, label, offset, Width.U32, EmptyHash, 0, uint.MaxValue, PerkBehaviorValueKind.Hash, description,
            choices, picker, required);

    private static Input TagInput(string key, string label, int offset, string picker, string description,
        bool required = true, (double, string)[]? choices = null) =>
        new(key, label, offset, Width.U32, NoTag, 0, uint.MaxValue, PerkBehaviorValueKind.Tag, description,
            choices, picker, required);

    private static Input Target(string key, string label, int offset, double value = 0) =>
        new(key, label, offset, Width.U8, value, 0, 3, PerkBehaviorValueKind.Integer,
            "Event objects exist only when an event activated the perk.", TargetChoices);

    // Properties, not fields: the spec table may be built before the other static fields.
    private static (double, string)[] TargetChoices =>
        [(0, "This object"), (1, "Owner"), (2, "Event object 1"), (3, "Event object 2")];
    private static (double, string)[] PositionChoices => [(0, "This object"), (1, "Event position")];

    /** Item conditions share one 112-byte form: own item, slot mask, label set at +16, filter kind at +96. */
    private static Spec ItemCondition(string key, string name, uint type, int opcode, string description, bool copyLabels,
        params Input[] extra) =>
        new(key, name, PerkBehaviorNodeKind.Condition, type, opcode, 112, description,
            [Flag("this_weapon", "Only this item", 8, 1, "Passes only for the item carrying this perk."),
             .. (extra.Length == 0 ? new[] { Mask("slots", "Slot mask", 9, 0, 15, "One bit per slot. 0 accepts any slot.") } : extra)],
            [new(88, Width.U32, NoTag), new(96, Width.U8, 0xFF)], [(80, 12)], LabelSet: copyLabels ? 16 : -1);

    private static Spec[] Build() =>
    [
        ItemCondition("item-active", "On item readied", 0x80803DF5, 16,
            "Passes when an item becomes active, for example when a weapon is drawn.", true),
        ItemCondition("item-equipped", "On item equipped", 0x80803DFA, 14,
            "Passes when an item is equipped into a slot. A new perk also sees items already equipped.", true),
        ItemCondition("item-unequipped", "On item unequipped", 0x80803DF7, 15,
            "Passes when an item is unequipped.", true),
        ItemCondition("weapon-tick", "On weapon counter tick", 0x80803DDE, 13,
            "Passes when a weapon counter rises. What the counter counts is not known.", false),
        ItemCondition("item-event", "On item event", 0x80803DDD, 18,
            "Passes on an item event per slot. Assumed: the weapon fired.", false),
        ItemCondition("reload", "On reload", 0x80803DE2, 19,
            "Passes when the active weapon reloads (assumed from perk names).", true,
            Flag("allow_off", "Pass when the event flag is off", 9, 1),
            Flag("allow_on", "Pass when the event flag is on", 10, 0, "What the flag means is not known."),
            Mask("slots", "Slot mask", 11, 0, 15, "One bit per slot. 0 accepts any slot."),
            Number("kill_window", "Recent kill window (seconds)", 12, 0, 0, 3600,
                "Needs a kill with that slot's weapon this recently. 0 turns it off.")),
        new("shot", "On shot resolved", PerkBehaviorNodeKind.Condition, 0x808029E0, 27, 152,
            "Passes when a shot hits or misses.",
            [Flag("this_weapon", "Only this weapon", 8, 1),
             Mask("slots", "Slot mask", 9, 0, 15, "One bit per slot. 0 accepts any slot."),
             new("mode", "Result", 144, Width.U8, 0, 0, 3, PerkBehaviorValueKind.Integer,
                 "Hit with test uses a native check whose meaning is not known.",
                 [(0, "Any"), (1, "Miss"), (2, "Hit"), (3, "Hit with test")])],
            [new(96, Width.U8, 0xFF), new(112, Width.U32, 0x20)], [(80, 12)], LabelSet: 16),
        new("owner-message", "On owner message", PerkBehaviorNodeKind.Condition, 0x80803DF8, 32, 104,
            "Passes on an owner message. Assumed: the owner died.", [],
            [new(80, Width.U32, NoTag), new(88, Width.U8, 0xFF)], [(72, 12)], LabelSet: 8),
        new("ability-used", "On ability used", PerkBehaviorNodeKind.Condition, 0x80803E01, 8, 32,
            "Passes when an ability in the mask is used.",
            [new("abilities", "Ability mask", 8, Width.U8, 1, 1, 255, PerkBehaviorValueKind.Integer,
                "Bit 0 grenade, bit 1 super, bit 7 class ability (assumed).")]),
        new("ability-start", "On ability event 7", PerkBehaviorNodeKind.Condition, 0x80803DFD, 7, 32,
            "Passes on ability message state 1 for an ability in the mask. The meaning is not known.",
            [new("abilities", "Ability mask", 8, Width.U8, 2, 1, 255, PerkBehaviorValueKind.Integer,
                "Bit 0 grenade, bit 1 super, bit 2 melee, bit 7 class ability (assumed).")]),
        new("ability-end", "On ability event 9", PerkBehaviorNodeKind.Condition, 0x80803E00, 9, 12,
            "Passes on ability message state 2 for an ability in the mask. The meaning is not known.",
            [new("abilities", "Ability mask", 8, Width.U8, 2, 1, 255, PerkBehaviorValueKind.Integer,
                "Bit 0 grenade, bit 1 super, bit 2 melee, bit 7 class ability (assumed).")]),
        new("ammo-pickup", "On ammo picked up", PerkBehaviorNodeKind.Condition, 0x80803DFB, 6, 12,
            "Passes when ammo is picked up for a weapon slot in the mask.",
            [new("slots", "Weapon slot mask", 8, Width.U8, 7, 1, 7, PerkBehaviorValueKind.Integer,
                 "1 kinetic, 2 energy, 4 power (assumed)."),
             Mask("ammo", "Ammo type mask", 9, 0, 7, "1 primary, 2 special, 4 heavy (assumed). 0 accepts any.")]),
        new("incident", "On incident", PerkBehaviorNodeKind.Condition, 0x80803DEA, 12, 16,
            "Passes on a world incident of the chosen type. Whether it checks the owner is not known.",
            [HashInput("incident", "Incident type", 8, "incident", "From the types installed perks use."),
             HashInput("role", "Role", 12, "role", "From the roles installed perks use.", false,
                 [(EmptyHash, "None")])]),
        new("event-2", "On gameplay event (second stream)", PerkBehaviorNodeKind.Condition, 0x80803DEC, 29, 12,
            "Passes when the perk receives the chosen hash on the second event stream.",
            [HashInput("event", "Event hash", 8, "event29", "From the hashes installed perks use.")]),
        new("kill-credit", "On kill credit", PerkBehaviorNodeKind.Condition, 0x80803DE5, 3, 288,
            "Passes once for each listed player when a kill is credited.",
            [Number("min_share", "Minimum damage share", 272, 0, 0, 1),
             Number("max_share", "Maximum damage share", 276, 1, 0, 1)],
            [new(80, Width.U32, NoTag), new(88, Width.U8, 0xFF), new(248, Width.U32, NoTag), new(256, Width.U8, 0xFF),
             new(276, Width.F32, 1), new(284, Width.U32, EmptyHash)], [(72, 12), (240, 12)]),
        new("owner-damaged", "On owner damaged", PerkBehaviorNodeKind.Condition, 0x80803DDC, 5, 200,
            "Passes when the owner takes damage.",
            [Number("min_share", "Minimum damage share", 152, 0, 0, 1),
             Number("distance", "Maximum attacker distance", 164, -1, -1, 100000, "-1 turns the test off."),
             Flag("self_damage", "Allow the owner as attacker", 156),
             Flag("attacker_gone", "Attacker must be gone", 157),
             Mask("slots", "Slot mask", 184, 0, 255, "0 accepts any slot."),
             Mask("parts", "Damage part mask", 185, 0, 15, "Bit i needs damage part i above zero. 0 turns it off."),
             new("types", "Damage types", 120, Width.U32, 0, 0, 255, PerkBehaviorValueKind.Integer,
                 "Bit per damage type: 2 Solar, 4 Arc, 8 Void (assumed). Used when Any damage type is off."),
             Flag("any_type", "Any damage type", 144, 1)],
            [new(112, Width.U32, NoTag), new(128, Width.U8, 0xFF), new(145, Width.U8, 1), new(160, Width.U32, EmptyHash),
             new(164, Width.F32, -1)], [(104, 12), (8, 16)], "types:8:120"),
        new("ability-gained", "On ability gained", PerkBehaviorNodeKind.Condition, 0x80803DFF, 10, 24,
            "Passes when the owner gains the chosen ability definition.",
            [TagInput("definition", "Ability definition", 16, "definitions", "An installed object definition.")],
            [new(16, Width.U32, NoTag)]),
        new("ability-lost", "On ability lost", PerkBehaviorNodeKind.Condition, 0x80803DFE, 11, 24,
            "Passes when the owner loses the chosen ability definition.",
            [TagInput("definition", "Ability definition", 16, "definitions", "An installed object definition.")],
            [new(16, Width.U32, NoTag)]),
        new("crouch", "On crouch change", PerkBehaviorNodeKind.Condition, 0x80803DF9, 22, 12,
            "Passes when a state flag changes to the chosen value. Assumed: crouching.",
            [Flag("state", "State", 8, 1)]),
        new("flag-25", "On flag 25 change", PerkBehaviorNodeKind.Condition, 0x80803DDF, 25, 12,
            "Passes when flag 25 changes to the chosen value. The flag's meaning is not known.",
            [Flag("state", "State", 8, 1)]),
        new("flag-24", "On action state change", PerkBehaviorNodeKind.Condition, 0x80803DE1, 24, 12,
            "Passes when an action state starts (true) or ends (false). Which actions raise it is not known.",
            [Flag("state", "State", 8, 1)]),
        new("player-killed", "On another player killed", PerkBehaviorNodeKind.Condition, 0x80803DFC, 21, 352,
            "Raised for every other player when a player is killed. Filters on the damage type.",
            [new("types", "Damage types", 312, Width.U32, 0, 0, 255, PerkBehaviorValueKind.Integer,
                 "Bit per damage type: 2 Solar, 4 Arc, 8 Void (assumed). Used when Any damage type is off."),
             Flag("any_type", "Any damage type", 336, 1),
             Flag("last_standing", "Only with no other living teammate", 344, 0,
                 "Fails while another player of the owner's group is alive (assumed).")],
            [new(80, Width.U32, NoTag), new(88, Width.U8, 0xFF), new(176, Width.U32, NoTag), new(184, Width.U8, 0xFF),
             new(304, Width.U32, NoTag), new(320, Width.U8, 0xFF), new(337, Width.U8, 1)],
            [(72, 12), (168, 12), (296, 12), (200, 16)], "types:200:312"),
        new("flag-36", "On flag 36 change", PerkBehaviorNodeKind.Condition, 0x80803DF6, 36, 12,
            "Passes when flag 36 changes to the chosen value. The flag's meaning is not known.",
            [Flag("state", "State", 8, 1)]),
        new("aim", "On aim change", PerkBehaviorNodeKind.Condition, 0x80803DDA, 23, 12,
            "Passes when aiming down sights starts or stops.",
            [Flag("state", "Aiming", 8, 1),
             Flag("grounded", "Not airborne", 9, description: "Assumed meaning."),
             Flag("this_weapon", "Only this weapon", 10, 1)]),
        new("spawned", "On player spawned", PerkBehaviorNodeKind.Condition, 0x80803DE0, 28, 12,
            "Passes when the player spawns for a reason in the mask.",
            [new("reasons", "Spawn reason mask", 8, Width.U8, 0x3C, 1, 255, PerkBehaviorValueKind.Integer,
                "Bit per spawn reason. Reason meanings are not known.")]),
        new("all-groups", "All groups pass", PerkBehaviorNodeKind.Condition, AllGroupsClass, 31, 24,
            "Passes when every group passes. A group passes when any of its conditions passes, "
            + "or passed within its window.", [], Ignore: [(8, 16)], Handler: "groups"),
        new("pickup", "On object picked up", PerkBehaviorNodeKind.Condition, 0x80803DE4, 34, 40,
            "Passes when the player picks up an object.",
            [TagInput("definition", "Pickup definition", 16, "definitions", "Any accepts every pickup.", false,
                 [(NoTag, "Any")]),
             Number("first_min", "First value minimum", 24, 0, -1000000, 1000000),
             Number("first_max", "First value maximum", 28, 1, -1000000, 1000000),
             Number("second_min", "Second value minimum", 32, 0, -1000000, 1000000),
             Number("second_max", "Second value maximum", 36, 1, -1000000, 1000000)],
            [new(16, Width.U32, NoTag), new(28, Width.F32, 1), new(36, Width.F32, 1)]),
        new("tracked-timer", "On tracked entry timer", PerkBehaviorNodeKind.Condition, 0x80802D02, 37, 12,
            "Passes when a tracked entry's timer runs out. Needs a Register a tracked target effect with the same name.",
            [HashInput("name", "Tracked name", 8, "tracked", "From the names installed perks use.")]),
        new("tracked-gone", "On tracked object gone", PerkBehaviorNodeKind.Condition, 0x80802D01, 38, 16,
            "Passes when a tracked object no longer exists and is far enough away. Needs a Register a tracked "
            + "target effect with the same name.",
            [HashInput("name", "Tracked name", 8, "tracked", "From the names installed perks use."),
             Number("distance", "Minimum distance", 12, 100, 0, 100000)],
            [new(8, Width.U32, EmptyHash)]),
        new("message", "On hashed message", PerkBehaviorNodeKind.Condition, 0x80802D00, 40, 144,
            "Passes on a message with the chosen hash. Its relation filters stay empty, so they pass.",
            [HashInput("hash", "Message hash", 8, "message", "Any accepts every message.", false, [(EmptyHash, "Any")])],
            [new(8, Width.U32, EmptyHash), new(16, Width.U32, 0x20), new(48, Width.U32, 0x20), new(80, Width.U32, 0x20),
             new(112, Width.U32, 0x20)]),
        new("weapon-event", "On weapon event 41", PerkBehaviorNodeKind.Condition, 0x808029E1, 41, 40,
            "Passes on weapon event 41 for a slot in the mask. The event's meaning is not known.",
            [Mask("slots", "Slot mask", 8, 0, 15, "0 accepts any slot.")]),
        new("finisher", "On finisher phase", PerkBehaviorNodeKind.Condition, 0x808029E6, 42, 12,
            "Passes when a finisher reaches the chosen phase.",
            [new("phase", "Phase", 8, Width.U8, 1, 0, 2, PerkBehaviorValueKind.Integer, "",
                [(0, "Phase 0"), (1, "Phase 1"), (2, "Phase 2")])],
            [new(8, Width.U8, 1)]),

        new("script", "Run a behavior script", PerkBehaviorNodeKind.Effect, 0x80802D0A, 48, 24,
            "Runs an installed behavior script with three target objects when the perk activates.",
            [Target("target_a", "Target A", 2), Target("target_b", "Target B", 3), Target("target_c", "Target C", 4),
             TagInput("root", "Behavior script", 16, "behaviors", "An installed script (class 8080941E).")],
            [new(16, Width.U32, NoTag)], [(8, 4)]),
        new("ability-key", "Add ability modifier key", PerkBehaviorNodeKind.Effect, 0x80803E1D, 7, 12,
            "Adds or removes a modifier key on one ability while the perk is active. It does nothing unless the "
            + "ability has the key.",
            [new("slot", "Ability", 2, Width.U8, 0, 0, 7, PerkBehaviorValueKind.Integer, "Slot meanings are assumed.",
                 [(0, "Grenade"), (1, "Super"), (2, "Melee"), (3, "Jump"), (4, "Slot 4"), (7, "Class ability")]),
             HashInput("key", "Modifier key", 4, "modifier", "From the keys installed perks use."),
             Flag("remove", "Remove instead of add", 8)],
            [new(4, Width.U32, EmptyHash)]),
        new("spawn-once", "Spawn an object once", PerkBehaviorNodeKind.Effect, 0x80803E43, 3, 24,
            "Creates an object when the perk activates. The object outlives the perk.",
            [Flag("snap", "Snap to ground", 2, description: "Assumed meaning."), Flag("face", "Face the event object", 3),
             new("position", "Position", 4, Width.U8, 0, 0, 1, PerkBehaviorValueKind.Integer, "", PositionChoices),
             TagInput("definition", "Object definition", 16, "definitions", "An installed object definition.")],
            [new(16, Width.U32, NoTag)]),
        new("orbs", "Create orbs", PerkBehaviorNodeKind.Effect, 0x80803E42, 5, 32,
            "Creates pickups for the player and allies when the perk activates. Assumed: Orbs of Light.",
            [new("position", "Position", 2, Width.U8, 0, 0, 1, PerkBehaviorValueKind.Integer, "", PositionChoices),
             new("count", "Count", 4, Width.I32, 1, 0, 100, PerkBehaviorValueKind.Integer),
             Number("value", "Value", 8, 1, 0, 10),
             TagInput("definition", "Definition", 24, "definitions", "Default uses the game's own definition.", false,
                 [(NoTag, "Default")])],
            [new(4, Width.I32, 1), new(8, Width.F32, 1), new(24, Width.U32, NoTag)]),
        new("ammo-crate", "Create an ammo drop", PerkBehaviorNodeKind.Effect, 0x80803E47, 13, 64,
            "Creates an ammo drop when the perk activates. Each category's weight sets how likely it is.",
            [new("position", "Position", 2, Width.U8, 1, 0, 1, PerkBehaviorValueKind.Integer, "", PositionChoices),
             Flag("this_player", "Drop for this player", 3, 1), Flag("allies", "Share with allies", 4),
             TagInput("attached", "Attached object", 16, "definitions", "None attaches nothing.", false, [(NoTag, "None")]),
             Number("primary_min", "Primary minimum", 24, 0, 0, 1000), Number("primary_max", "Primary maximum", 28, 0, 0, 1000),
             Number("primary_weight", "Primary weight", 32, 0, 0, 1000),
             Number("special_min", "Special minimum", 36, 0, 0, 1000), Number("special_max", "Special maximum", 40, 0, 0, 1000),
             Number("special_weight", "Special weight", 44, 1, 0, 1000),
             Number("heavy_min", "Heavy minimum", 48, 0, 0, 1000), Number("heavy_max", "Heavy maximum", 52, 0, 0, 1000),
             Number("heavy_weight", "Heavy weight", 56, 0, 0, 1000)],
            [new(3, Width.U8, 1), new(16, Width.U32, NoTag)]),
        AmmoGrant("ammo-rounds", "Add ammo (rounds)", 0x80803E3F, 14, Width.I32,
            "Adds whole rounds to each weapon slot that passes.", "Multiply by rounds per shot", "Assumed meaning."),
        AmmoGrant("ammo-fraction", "Add ammo (fraction)", 0x80803E3E, 15, Width.F32,
            "Adds a fraction of capacity to each weapon slot that passes.", "Base is magazine size"),
        new("refill", "Refill magazine from reserves", PerkBehaviorNodeKind.Effect, 0x808029EC, 16, 392,
            "Moves a fraction of the magazine from reserves into the magazine.",
            [Flag("overflow", "Allow over magazine size", 104), Flag("magazine_base", "Base is magazine size", 105, 1),
             Flag("feedback", "Post reload feedback", 106, description: "Assumed meaning."),
             new("input", "Input", 107, Width.I8, -1, -1, 12, PerkBehaviorValueKind.Integer,
                 "Meanings other than the perk counter are assumed.",
                 [(-1, "None"), (0, "Perk counter"), (1, "Missing rounds"), (2, "Rounds in magazine")]),
             Flag("normalize", "Divide by magazine size", 108),
             Number("active_fraction", "Active weapon fraction", -1, 0, -10, 10),
             Number("this_fraction", "This weapon fraction", -1, 0, -10, 10),
             Number("slot0_fraction", "Slot 0 fraction", -1, 0, -10, 10),
             Number("slot1_fraction", "Slot 1 fraction", -1, 0, -10, 10),
             Number("slot2_fraction", "Slot 2 fraction", -1, 0, -10, 10)],
            [new(80, Width.U32, NoTag), new(88, Width.U8, 0xFF), new(107, Width.U8, 0xFF)], [(72, 12), (112, 280)],
            "fractions"),
        new("damage-resist", "Owner damage multiplier", PerkBehaviorNodeKind.Effect, ResistClass,
            33, 184, "Multiplies the owner's damage by this value while the perk is active. Assumed: damage taken.",
            [Number("multiplier", "Multiplier", 4, 1, 0, 10),
             Number("distance", "Maximum source distance", 12, -1, -1, 100000, "-1 turns the test off."),
             new("types", "Damage types", 128, Width.U32, 0, 0, 255, PerkBehaviorValueKind.Integer,
                 "Bit per damage type: 2 Solar, 4 Arc, 8 Void (assumed). Used when Any damage type is off."),
             Flag("any_type", "Any damage type", 152, 1)],
            [new(8, Width.U8, 0xFF), new(12, Width.F32, -1), new(120, Width.U32, NoTag), new(136, Width.U8, 0xFF),
             new(153, Width.U8, 1)], [(112, 12), (16, 16), (144, 8), (160, 24)], "types:16:128;selfref"),
        new("extend-timers", "Extend timers on event", PerkBehaviorNodeKind.Effect, ExtendTimersClass, 32, 40,
            "While the perk is active, a passing condition adds seconds to running timers, up to the cap.",
            [Number("seconds", "Seconds added", 4, 1, -3600, 3600), Number("cap", "Cap (seconds)", 8, 10, 0, 3600)],
            Ignore: [(16, 24)], Handler: "timer-conditions"),
        new("damage-labels", "Add labels to damage", PerkBehaviorNodeKind.Effect, 0x80803E1A, 37, 208,
            "Adds a label to damage records that pass the type filter.",
            [new("label", "Label", -1, Width.U32, 0, 0, 320.0 * 4294967296 - 1, PerkBehaviorValueKind.Label,
                 "From the installed label table.", Picker: "labels", Required: true),
             new("types", "Damage types", 120, Width.U32, 0, 0, 255, PerkBehaviorValueKind.Integer,
                 "Bit per damage type: 2 Solar, 4 Arc, 8 Void (assumed). Used when Any damage type is off."),
             Flag("any_type", "Any damage type", 144, 1)],
            [new(112, Width.U32, NoTag), new(128, Width.U8, 0xFF), new(145, Width.U8, 1)],
            [(104, 12), (8, 16), (152, 56)], "types:8:120;labels:152:168"),
        new("event-labels", "Add labels to item events", PerkBehaviorNodeKind.Effect, 0x8080281C, 54, 168,
            "Adds a label to event 54 records on this perk's items.",
            [new("label", "Label", -1, Width.U32, 0, 0, 320.0 * 4294967296 - 1, PerkBehaviorValueKind.Label,
                "From the installed label table.", Picker: "labels", Required: true)],
            [new(80, Width.U32, NoTag), new(88, Width.U8, 0xFF), new(160, Width.U8, 1)], [(72, 12), (104, 56)],
            "labels:104:120"),
        new("projectile", "Override projectile", PerkBehaviorNodeKind.Effect, 0x80803E12, 26, 24,
            "Writes an object definition into this weapon while the perk is active. Assumed: the projectile.",
            [TagInput("definition", "Object definition", 16, "definitions", "An installed object definition.")],
            [new(16, Width.U32, NoTag)], [(8, 8)]),
        new("weapon-floats", "Set three weapon values", PerkBehaviorNodeKind.Effect, 0x80803E0B, 29, 16,
            "Sets three weapon values while the perk is active. What they control is not known.",
            [Number("first", "First value", 4, 0, -1000000, 1000000), Number("second", "Second value", 8, 0, -1000000, 1000000),
             Number("third", "Third value", 12, 0, -1000000, 1000000)]),
        new("sim-object", "Post a sim object", PerkBehaviorNodeKind.Effect, 0x80803E1E, 43, 8,
            "Posts a sim object or incident for a player owner when the perk activates.",
            [HashInput("hash", "Definition hash", 4, "sim", "From the hashes installed perks use.")],
            [new(4, Width.U32, EmptyHash)]),
        new("weapon-mode", "Set weapon element choice", PerkBehaviorNodeKind.Effect, 0x80803E41, 6, 4,
            "Sets a weapon byte while the perk is active. Assumed: the element choice.",
            [new("value", "Value", 2, Width.I8, 0, -128, 127, PerkBehaviorValueKind.Integer),
             Flag("keep", "Keep after the perk ends", 3)]),
        new("weapon-pair", "Set two weapon bytes", PerkBehaviorNodeKind.Effect, 0x80803E0C, 28, 24,
            "Sets two weapon bytes while the perk is active. What they control is not known.",
            [Mask("first", "First value", 2, 0, 255, ""), Mask("second", "Second value", 3, 0, 255, "")]),
        new("item-flag", "Add a named flag to items", PerkBehaviorNodeKind.Effect, 0x80803E39, 41, 8,
            "Adds a named flag to items while the perk is active. Properties can read the same name.",
            [Flag("this_item", "This item", 2, 1), Mask("slots", "Other slots", 3, 0, 7, "Bits 0 to 2 add slot items."),
             HashInput("flag", "Flag name hash", 4, "", "Any name hash.")],
            [new(2, Width.U8, 1), new(4, Width.U32, EmptyHash)]),
        new("sword-guard", "Enable sword guard", PerkBehaviorNodeKind.Effect, 0x808029EB, 27, 2,
            "Turns on a weapon behavior while the perk is active. Assumed: sword guard, as in every installed use.", []),
        new("mode-tag", "Set weapon mode tag", PerkBehaviorNodeKind.Effect, 0x80803E13, 25, 8,
            "Sets a mode tag on this weapon while the perk is active. While-state rows can test it.",
            [HashInput("tag", "Mode tag hash", 4, "", "Any name hash.")], [new(4, Width.U32, EmptyHash)]),
        new("channel-hash", "Set a channel hash", PerkBehaviorNodeKind.Effect, 0x80803E1C, 35, 12,
            "Sets a hash on a component channel while the perk is active. What it controls is not known.",
            [Target("target", "Target", 2),
             new("channel", "Channel", 3, Width.U8, 14, 3, 14, PerkBehaviorValueKind.Integer, "", [(3, "3"), (14, "14")]),
             HashInput("hash", "Hash", 4, "channel", "From the hashes installed perks use.", false, [(EmptyHash, "Default")]),
             Flag("owner_too", "Also the owner", 8)],
            [new(3, Width.U8, 14), new(4, Width.U32, EmptyHash)]),
        new("component", "Apply a component config", PerkBehaviorNodeKind.Effect, 0x80803E46, 4, 24,
            "Applies an installed component config to the target when the perk activates.",
            [Target("target", "Target", 2),
             TagInput("config", "Component config", 16, "configs", "An installed config (class 80809C36).")],
            [new(16, Width.U32, NoTag)], [(8, 8)]),
        new("weapon-count", "Raise a weapon flag count", PerkBehaviorNodeKind.Effect, 0x80803E0D, 30, 3,
            "Raises a weapon counter while the perk is active. What it controls is not known.",
            [Flag("enable", "Enabled", 2, 1)]),
        new("stat-scale", "Scale component values", PerkBehaviorNodeKind.Effect, 0x808029F2, 53, 96,
            "When the perk activates, changes the named component values on the target by an expression of the input.",
            [new("name", "Component name", -1, Width.U32, EmptyHash, 0, uint.MaxValue, PerkBehaviorValueKind.Hash,
                 "From the names installed perks use.", Picker: "scaled", Required: true),
             Number("cap", "Cap", 24, -1, -1, 1000000, "-1 turns the cap off."),
             new("mode", "Mode", 90, Width.U8, 0, 0, 2, PerkBehaviorValueKind.Integer, "",
                 [(0, "Add"), (1, "Set"), (2, "Multiply")]),
             Target("source", "Input object", 91, 0), Target("target", "Target object", 92, 0),
             .. ExpressionInputs(1).Select(property => new Input(property.Key, property.Label,
                 property.Key == "input" ? 88 : -1, property.Key == "input" ? Width.I8 : Width.F32, property.Value,
                 property.Minimum, property.Maximum, property.Kind, property.Description,
                 [.. property.Choices.Select(choice => (choice.Value, choice.Label))])),
             Flag("normalize", "Divide by magazine size", 89)],
            [new(24, Width.F32, -1)], [(8, 16), (32, 56)], "names:8;expression:40"),
        new("register-target", "Register a tracked target", PerkBehaviorNodeKind.Effect, 0x80802D0D, 49, 8,
            "Stores a target object under a name that tracked-entry conditions read.",
            [Target("target", "Target", 2, 3), HashInput("name", "Tracked name", 4, "tracked", "From the names installed perks use.")],
            [new(2, Width.U8, 3), new(4, Width.U32, EmptyHash)]),
        new("radar", "Set radar values", PerkBehaviorNodeKind.Effect, 0x80803E23, 18, 16,
            "Sets radar values while the perk is active. -1 leaves a value unchanged.",
            [Number("first", "First value", 4, -1, -1, 100000), Number("second", "Second value", 8, -1, -1, 100000),
             Number("third", "Third value", 12, -1, -1, 100000)],
            [new(4, Width.F32, -1), new(8, Width.F32, -1), new(12, Width.F32, -1)]),
        new("radar-count", "Raise the radar counter", PerkBehaviorNodeKind.Effect, 0x80803E24, 20, 3,
            "Raises a radar counter while the perk is active.", [Flag("enable", "Enabled", 2, 1)], [new(2, Width.U8, 1)]),
        new("team-stat", "Add to a team stat", PerkBehaviorNodeKind.Effect, 0x808029F1, 52, 12,
            "Adds an amount to the owner's team stat while the perk is active.",
            [HashInput("stat", "Stat name", 4, "stat", "From the names installed perks use."), Number("amount", "Amount", 8, 1, -1000000, 1000000)],
            [new(4, Width.U32, EmptyHash)]),
        new("movement", "Movement mode", PerkBehaviorNodeKind.Effect, 0x80803E19, 36, 16,
            "Puts the owner into a movement mode with these settings when the perk activates. Assumed: hover. "
            + "Speed limits are decoded; the other values are copied into the mode.",
            [.. MovementInputs.Select(field => field.Input)], Ignore: [(8, 8)], Handler: "movement"),
        new("weapon-override", "Push a weapon override byte", PerkBehaviorNodeKind.Effect, 0x80802D03, 51, 3,
            "Pushes a byte onto this weapon's override stack while the perk is active.", [Mask("value", "Value", 2, 0, 255, "")]),
        new("slot-bytes", "Set slot weapon byte", PerkBehaviorNodeKind.Effect, 0x80803E0F, 22, 5,
            "Sets a byte on slot weapons while the perk is active. What it controls is not known.",
            [Flag("this_slot", "This perk's slot", 2, 1), Mask("slots", "Other slots", 3, 0, 15, "One bit per slot."),
             Mask("value", "Value", 4, 0, 255, "")]),
        new("owner-flag", "Set owner flag", PerkBehaviorNodeKind.Effect, 0x80803E4C, 0, 2,
            "Sets a flag on the owner while the perk is active. What it controls is not known.", []),
        new("slot-flag", "Set slot flag", PerkBehaviorNodeKind.Effect, 0x80803E14, 24, 2,
            "Sets a flag on this perk's slot while the perk is active. Assumed: drop the magazine.", []),
        new("radar-byte", "Set radar byte", PerkBehaviorNodeKind.Effect, 0x80803E25, 17, 3,
            "Sets a radar byte while the perk is active.", [Mask("value", "Value", 2, 1, 255, "")], [new(2, Width.U8, 1)])
    ];

    /** Ops 14 and 15 share one form; only the value width and flag +106 differ. */
    private static Spec AmmoGrant(string key, string name, uint type, int opcode, Width width, string description,
        string scaleLabel, string scaleNote = "")
    {
        var kind = width == Width.F32 ? PerkBehaviorValueKind.Float : PerkBehaviorValueKind.Integer;
        string[] labels = ["This weapon", "Slot 0", "Slot 1", "Slot 2", "Primary ammo", "Special ammo", "Heavy ammo"];
        string[] keys = ["this_weapon", "slot0", "slot1", "slot2", "primary", "special", "heavy"];
        return new(key, name, PerkBehaviorNodeKind.Effect, type, opcode, 136, description,
            [Flag("magazine", "Into the magazine", 104), Flag("overflow", "Allow over magazine size", 105),
             Flag("scale", scaleLabel, 106, description: scaleNote), Flag("counter", "Multiply by the perk counter", 107),
             .. keys.Select((value, i) => new Input(value, labels[i], 108 + i * 4, width, 0, -1000000, 1000000, kind))],
            [new(80, Width.U32, NoTag), new(88, Width.U8, 0xFF)], [(72, 12)]);
    }
}
