using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** Native operations stay fixed; authored graphs select their inputs and phase membership. */
public static partial class NativePerkBehavior
{
    public const uint Class = 0x808040B5;
    private const int RootSize = 208, MaximumNodes = 128;
    private const uint ArrayMarker = 0x80809FBD, ConditionRowClass = 0x808040BA;
    private const uint EffectRowClass = 0x808040AC;
    // Root +16 is the feedback array; a status row puts a buff in the HUD tray.
    private const int FeedbackField = 16, StatusSize = 24;
    private const uint FeedbackRowClass = 0x80804083, StatusClass = 0x80804085;
    // Status mask bit 0x02 shows the entry while the perk is active.
    private const byte StatusWhileActive = 0x02;
    // Root +88 holds cooldown conditions and +104 stack branches; neither is shown as a phase.
    private const int CooldownField = 88, StackField = 104, TimerEffectsField = 160;

    /** Status labels already present in both the HUD icon library and the label strings. */
    public static IReadOnlyList<PerkBehaviorChoice> HudLabels { get; } =
        [new(0xF2ED5B38, "Ammo Finder")];

    public static IReadOnlyList<PerkBehaviorPrimitive> Primitives { get; } =
    [
        new("always", "When activated", PerkBehaviorNodeKind.Condition, AlwaysClass,
            "Passes when the perk enters this phase. Probability can limit activation.", true, 0),
        new("timer", "After a delay", PerkBehaviorNodeKind.Condition, TimerClass,
            "Passes after this phase's timer expires.", true, 1),
        new("kill", "On kill", PerkBehaviorNodeKind.Condition, KillClass,
            "Passes when the owner kills a target.", true, 2),
        new("damage", "On damage dealt", PerkBehaviorNodeKind.Condition, DamageClass,
            "Passes when the owner damages a target.", true, 4),
        new("unequip", "On item stowed", PerkBehaviorNodeKind.Condition, UnequipClass,
            "Passes when an item leaves its active or equipped state, for example when the weapon is stowed.", true, 17),
        new("while-state", "While a state holds", PerkBehaviorNodeKind.Condition, StateClass,
            "Tested on every update. Passes while an object property is in range.", true, 20),
        new("counter", "Counter reaches a threshold", PerkBehaviorNodeKind.Condition, CounterClass,
            "Each row changes the perk counter when its condition passes. Passes when the counter reaches the threshold.", true, 26),
        new("event", "On gameplay event", PerkBehaviorNodeKind.Condition, EventClass,
            "Passes when the perk receives the chosen event hash. Which game event each hash is, is not known.", true, 30),
        new("state-kill", "On kill while a state holds", PerkBehaviorNodeKind.Condition, GatedClass,
            "Passes on a kill while an object property is in range. Both probabilities apply.", true, 35),
        new("state-damage", "On damage while a state holds", PerkBehaviorNodeKind.Condition, GatedClass,
            "Passes on damage dealt while an object property is in range. Both probabilities apply.", true, 35),
        new("state-unequip", "On item stowed while a state holds", PerkBehaviorNodeKind.Condition, GatedClass,
            "Passes when an item is stowed while an object property is in range. Both probabilities apply.", true, 35),
        new("state-event", "On gameplay event while a state holds", PerkBehaviorNodeKind.Condition, GatedClass,
            "Passes on the chosen event hash while an object property is in range. Both probabilities apply.", true, 35),
        new("spawn", "Spawn an object", PerkBehaviorNodeKind.Effect, SpawnClass,
            "Creates an object from an existing object definition while the perk is active.", true, 1),
        new("driven-spawn", "Spawn an object driven by a value", PerkBehaviorNodeKind.Effect, DrivenSpawnClass,
            "Creates an object and pushes a value into it whenever the perk counter or a condition changes. "
            + "What the object does with the value is not known.", true, 2),
        new("ability-energy", "Grant ability energy", PerkBehaviorNodeKind.Effect, EnergyClass,
            "Adds a fraction of a full charge to one ability once, when the perk activates.", true, 8),
        new("property", "Change object property", PerkBehaviorNodeKind.Effect, PropertyClass,
            "Sets, adds, subtracts or multiplies a named object property. Native code performs the write.", true, 10),
        new("ammo-drop", "Adjust ammo drops", PerkBehaviorNodeKind.Effect, AmmoDropClass,
            "Changes native primary, special and heavy ammo drop chances while the perk is active.", true, 11),
        new("damage-multiplier", "Damage multiplier", PerkBehaviorNodeKind.Effect, DamageScaleClass,
            "Multiplies damage by one plus the bonus while the perk is active. Assumed: who the damage belongs to "
            + "and the element meanings.", true, 40),
        new("perk-counter", "Set perk counter", PerkBehaviorNodeKind.Effect, CounterEffectClass,
            "Sets or adds to the perk counter when the perk activates.", true, 42),
        new("transmat", "Transmat or Ghost effect", PerkBehaviorNodeKind.Effect, TransmatClass,
            "Selects the player's transmat or Ghost effect while this perk is present.", true, 47),
        .. SpecTable.All.Select(spec => new PerkBehaviorPrimitive(spec.Key, spec.Name, spec.Kind, spec.Class,
            spec.Description, spec.CanCreate, spec.Opcode))
    ];

    /** Condition kinds a new counter row can hold. */
    public static IReadOnlyList<string> CounterRowPrimitives =>
        [.. StateGatedKeys, .. EventPrimitives];

    /** True for the kinds an in-game perk has used; every other kind is untested in game. */
    public static bool IsTested(string primitive) => primitive is "always" or "timer" or "ammo-drop";

    /** Creates an empty graph. */
    public static PerkBehaviorGraph New() => new();

    /** Defaults are authored inputs; no installed perk supplies the new node's bytes. */
    public static PerkBehaviorNode CreateNode(string primitive)
    {
        if (primitive == GroupKey) return CreateGroup();
        var spec = PrimitiveOf(primitive) ?? throw new InvalidDataException("Choose a supported native operation.");
        var node = Blank(primitive, spec);
        if (primitive.StartsWith(StatePrefix, StringComparison.Ordinal))
            node.Children.Add(CreateNode(primitive[StatePrefix.Length..]));
        return node;
    }

    /** A counter row is its condition plus the row's own inputs. */
    public static PerkBehaviorNode CreateRow(string primitive)
    {
        if (!CounterRowPrimitives.Contains(primitive))
            throw new InvalidDataException("A counter row needs a decoded condition.");
        var node = CreateNode(primitive);
        node.Properties.AddRange(RowInputs());
        return node;
    }

    /** Undecoded nodes keep their source locations so unrelated edits preserve every byte. */
    public static PerkBehaviorGraph Decompile(ReadOnlyMemory<byte> bytes)
    {
        RequireRoot(bytes);
        var graph = new PerkBehaviorGraph { OriginalBytes = bytes.ToArray(), ProgramTag = FindProgramTag(bytes) };
        var data = new TagReader(bytes);
        ReadPhase(32, graph.Activation, PerkBehaviorNodeKind.Condition);
        ReadPhase(56, graph.Effects, PerkBehaviorNodeKind.Effect);
        ReadPhase(72, graph.Deactivation, PerkBehaviorNodeKind.Condition);
        graph.Hud = FindHud(bytes)?.Hud;
        if (data.U64(FeedbackField) != 0 && graph.Hud is null || data.U64(CooldownField) != 0 || data.U64(StackField) != 0)
            graph.Diagnostics.Add("HUD, cooldown and stack branches kept; not shown.");
        return graph;

        void ReadPhase(int field, List<PerkBehaviorNode> nodes, PerkBehaviorNodeKind kind)
        {
            var array = data.Array(field, kind == PerkBehaviorNodeKind.Condition ? ConditionRowClass : EffectRowClass, 8);
            if (array.Count > MaximumNodes) throw new InvalidDataException("The perk phase exceeds the node limit.");
            for (int i = 0; i < array.Count; i++)
            {
                int target = data.Relative(array.Data + i * 8);
                nodes.Add(ReadNode(bytes, target, kind, Role.Top, $"{field}:{i}:{target}", graph.Diagnostics));
            }
        }
    }

    /**
     * Validation checks native phase types, input ranges and the provenance of preserved nodes.
     * @param source Decompile of graph.OriginalBytes, when the caller already has it; null decompiles here.
     */
    public static IReadOnlyList<string> Validate(PerkBehaviorGraph graph, PerkBehaviorGraph? source = null)
    {
        var problems = new List<string>();
        if (graph.Version != 1) problems.Add("This perk graph version is unsupported.");
        if (graph.OriginalBytes.Length == 0 && graph.Activation.Count == 0)
            problems.Add("Add a condition that activates the perk.");
        if (graph.OriginalBytes.Length == 0 && graph.Effects.Count == 0)
            problems.Add("Add an effect for the perk to perform.");
        var top = graph.Activation.Concat(graph.Effects).Concat(graph.Deactivation).ToArray();
        var nodes = top.SelectMany(Flatten).ToArray();
        if (top.Length > MaximumNodes) problems.Add($"A perk graph supports up to {MaximumNodes} nodes.");
        if (nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != nodes.Length)
            problems.Add("Each perk node needs a unique identity.");
        if (graph.OriginalBytes.Length == 0) source = null;
        else source ??= Decompile(graph.OriginalBytes);
        var originals = source?.Activation.Concat(source.Effects).Concat(source.Deactivation).SelectMany(Flatten)
            .ToDictionary(node => node.Id, StringComparer.Ordinal);
        // Extend-timers conditions under effects take no index, so only phase conditions count here.
        if (source is not null && graph.Activation.Concat(graph.Deactivation).SelectMany(Flatten)
                .Any(node => node.OriginalOffset < 0 && node.Primitive != GroupKey)
            && (new TagReader(graph.OriginalBytes).U64(StackField) != 0 || ConditionIndices(graph.OriginalBytes) is null))
            problems.Add("Adding conditions needs a perk without stack branches.");
        if (graph.Effects.SelectMany(Flatten).Any(node => node.NativeClass == ResistClass && node.OriginalOffset < 0)
            && graph.ProgramTag == 0)
            problems.Add("A new owner damage multiplier needs the program's tag.");
        if (graph.Effects.Skip(64).Any(node => node.NativeClass == ExtendTimersClass))
            problems.Add("Extend-timers effects must be among the first 64 effects.");
        if (source is not null && source.Activation.Count > 0 && graph.Activation.Count == 0)
            problems.Add("Keep an explicit activation condition when editing this perk.");
        if (graph.Hud is { } hud)
        {
            if (!HudLabels.Any(label => label.Value == hud.Label))
                problems.Add("Choose a HUD label from the installed status labels.");
            if (source is { Hud: null } && new TagReader(graph.OriginalBytes).U64(FeedbackField) != 0)
                problems.Add("This perk already has native HUD feedback, which is preserved.");
            if (hud.ShowTimer && !graph.Deactivation.Any(node => node.Primitive == "timer"))
                problems.Add("The HUD timer needs a deactivation timer.");
        }
        foreach (var shared in nodes.Where(node => node.OriginalOffset >= 0).GroupBy(node => node.OriginalOffset))
        {
            var first = shared.First().Properties.Select(property => (property.Key, property.Value));
            if (shared.Skip(1).Any(node => !node.Properties.Select(property => (property.Key, property.Value)).SequenceEqual(first)))
                problems.Add("Shared native nodes must keep the same input values.");
        }
        foreach (var node in top)
        {
            bool effect = graph.Effects.Contains(node);
            if (node.Kind != (effect ? PerkBehaviorNodeKind.Effect : PerkBehaviorNodeKind.Condition))
                problems.Add($"{node.Title} is in the wrong phase.");
            ValidateNode(node, Role.Top, null);
        }
        return problems;

        void ValidateNode(PerkBehaviorNode node, Role role, PerkBehaviorNode? parent)
        {
            List<PerkBehaviorProperty> expected;
            PerkBehaviorNode? original = null;
            if (node.OriginalOffset >= 0)
            {
                if (originals is null || !originals.TryGetValue(node.Id, out original)
                    || original.OriginalOffset != node.OriginalOffset
                    || original.NativeClass != node.NativeClass || original.Primitive != node.Primitive
                    || original.Kind != node.Kind || !original.NativeBytes.SequenceEqual(node.NativeBytes)
                    || !original.Children.Select(child => child.Id).SequenceEqual(node.Children.Select(child => child.Id)))
                { problems.Add($"{node.Title} no longer matches its source bytes."); return; }
                expected = original.Properties;
                if (!original.Editable)
                    foreach (var spec in expected) spec.Editable = false;
            }
            else
            {
                if (parent is { OriginalOffset: >= 0 })
                { problems.Add($"{parent.Title} keeps its installed rows; it cannot take new ones."); return; }
                bool allowed = role switch
                {
                    Role.Inner => parent is not null && parent.Primitive == StatePrefix + node.Primitive,
                    Role.Top => node.Primitive != GroupKey,
                    _ => parent is not null && ChildPrimitives(parent).Contains(node.Primitive)
                };
                bool known = node.Primitive == GroupKey ? node.NativeClass == GroupClass && role == Role.Groups
                    : PrimitiveOf(node.Primitive) is { CanCreate: true } kind && kind.NativeClass == node.NativeClass
                        && kind.Kind == node.Kind;
                if (!allowed || !known)
                { problems.Add($"{node.Title} cannot be created from scratch here."); return; }
                expected = role == Role.Row ? CreateRow(node.Primitive).Properties : CreateNode(node.Primitive).Properties;
                problems.AddRange(Semantics(node));
            }
            if (expected.Count != node.Properties.Count
                || node.Properties.Select(property => property.Key).Distinct().Count() != node.Properties.Count)
                problems.Add($"{node.Title} has missing or duplicate inputs.");
            foreach (var spec in expected)
            {
                var value = node.Properties.FirstOrDefault(property => property.Key == spec.Key);
                if (value is null) continue;
                if (!double.IsFinite(value.Value) || value.Value < spec.Minimum || value.Value > spec.Maximum
                    || spec.Kind != PerkBehaviorValueKind.Float && value.Value != Math.Truncate(value.Value)
                    || spec.Kind == PerkBehaviorValueKind.Boolean && value.Value is not (0 or 1)
                    || node.OriginalOffset < 0 && spec.Picker.Length == 0 && spec.Choices.Count != 0
                        && spec.Choices.All(choice => choice.Value != value.Value))
                    problems.Add($"{node.Title}: {spec.Label} is outside its supported range.");
                else if (!spec.Editable && BitConverter.DoubleToInt64Bits(value.Value) != BitConverter.DoubleToInt64Bits(spec.Value))
                    problems.Add($"{node.Title}: {spec.Label} is read-only.");
            }
            problems.AddRange(FilterProblems(node, original));
            foreach (var child in node.Children)
            {
                if (child.Kind != PerkBehaviorNodeKind.Condition) problems.Add($"{child.Title} is not a condition.");
                ValidateNode(child, ChildRole(node), node);
            }
        }
    }

    /** Existing payloads stay in place; structural edits append arrays and fresh native node records. */
    public static byte[] Compile(PerkBehaviorGraph graph)
    {
        bool fresh = graph.OriginalBytes.Length == 0;
        var source = fresh ? null : Decompile(graph.OriginalBytes);
        var problems = Validate(graph, source);
        if (problems.Count != 0) throw new InvalidDataException(string.Join("\n", problems));
        var writer = new PerkWriter(fresh ? NewRoot() : graph.OriginalBytes) { ProgramTag = graph.ProgramTag };
        List<int> indices = fresh ? [] : ConditionIndices(graph.OriginalBytes) ?? [];
        // Condition +7 is unique within the program; new conditions continue after the highest.
        int nextIndex = indices.Count == 0 ? 0 : indices.Max() + 1;
        WritePhase(32, graph.Activation, source?.Activation, ConditionRowClass);
        WritePhase(56, graph.Effects, source?.Effects, EffectRowClass);
        WritePhase(72, graph.Deactivation, source?.Deactivation, ConditionRowClass);
        writer.U64(136, writer.Read64(136) | Mask(graph.Activation));
        writer.U64(144, writer.Read64(144) | Mask(graph.Deactivation));
        // Root +160 has bit i set when effect i extends timers; the event walker visits only those.
        writer.U64(TimerEffectsField, Enumerable.Range(0, Math.Min(64, graph.Effects.Count))
            .Where(i => graph.Effects[i].NativeClass == ExtendTimersClass).Aggregate(0UL, (mask, i) => mask | 1UL << i));
        if (fresh)
        {
            // Every stock root sets these; their reader is unknown. Values follow the stock pattern.
            writer.U8(204, 1);
            writer.U8(205, checked((byte)graph.Activation.Concat(graph.Deactivation).Count(node => node.Primitive == "timer")));
        }
        // A HUD row Studio wrote is rewritten in place; removing it empties the feedback array.
        var installedHud = fresh ? null : FindHud(graph.OriginalBytes);
        if (graph.Hud is { } hud)
        {
            int status = installedHud?.At ?? writer.Node(StatusClass, StatusSize);
            writer.Copy(status, StatusBytes(hud));
            if (installedHud is null)
            {
                int rows = writer.Array(FeedbackField, FeedbackRowClass, 1, 8);
                writer.I64(rows, status - rows);
            }
        }
        else if (installedHud is not null) writer.Array(FeedbackField, FeedbackRowClass, 0, 8);
        return writer.Finish();

        void WritePhase(int field, List<PerkBehaviorNode> nodes, List<PerkBehaviorNode>? original, uint rowClass)
        {
            var offsets = new List<int>();
            foreach (var node in nodes)
            {
                int target;
                if (node.OriginalOffset >= 0)
                {
                    target = node.OriginalOffset;
                    PatchNode(writer, graph.OriginalBytes, node, target, Role.Top);
                }
                else target = WriteNode(writer, node, Role.Top, ref nextIndex);
                offsets.Add(target);
            }
            if (original is not null && offsets.SequenceEqual(original.Select(node => node.OriginalOffset))) return;
            int rows = writer.Array(field, rowClass, nodes.Count, 8);
            for (int i = 0; i < offsets.Count; i++) writer.I64(rows + i * 8, offsets[i] - rows - i * 8);
        }
    }

    /** The status row Compile writes for a HUD entry. */
    private static byte[] StatusBytes(PerkHudStatus hud)
    {
        byte[] status = new byte[StatusSize];
        status[0] = StatusWhileActive;
        status[1] = byte.MaxValue;
        BinaryPrimitives.WriteUInt32LittleEndian(status.AsSpan(4), hud.Label);
        status[20] = byte.MaxValue;
        status[21] = 1;
        status[22] = hud.ShowTimer ? (byte)1 : (byte)0;
        return status;
    }

    /** The HUD entry and its row offset when root +16 holds exactly one row in the form Compile writes. */
    private static (PerkHudStatus Hud, int At)? FindHud(ReadOnlyMemory<byte> bytes)
    {
        var data = new TagReader(bytes);
        if (!data.TryArray(FeedbackField, FeedbackRowClass, 8, out var rows) || rows.Count != 1) return null;
        int at = Target(data, rows.Data);
        if (at < 4 || !data.Has(at, StatusSize) || data.U32(at - 4) != StatusClass) return null;
        var hud = new PerkHudStatus { Label = data.U32(at + 4), ShowTimer = data.U8(at + 22) == 1 };
        if (!HudLabels.Any(label => label.Value == hud.Label)
            || !bytes.Span.Slice(at, StatusSize).SequenceEqual(StatusBytes(hud))) return null;
        return (hud, at);
    }

    /** A phase mask needs every event its conditions match, nested rows and inner conditions included. */
    private static ulong Mask(IEnumerable<PerkBehaviorNode> nodes) =>
        nodes.Aggregate(0UL, (mask, node) => mask | Bits(node));

    private static ulong Bits(PerkBehaviorNode node)
    {
        int opcode = node.Primitive == GroupKey ? -1 : node.NativeBytes.Length >= 6 ? node.NativeBytes[5]
            : PrimitiveOf(node.Primitive)?.Opcode ?? -1;
        ulong bits = opcode is >= 0 and < 64 ? 1UL << opcode : 0;
        return node.Children.Aggregate(bits, (mask, child) => mask | Bits(child));
    }

    /** Every condition's +7 index, nested ones included; null when a payload cannot be walked. */
    private static List<int>? ConditionIndices(ReadOnlyMemory<byte> bytes)
    {
        var data = new TagReader(bytes);
        var seen = new HashSet<int>();
        var result = new List<int>();
        try
        {
            foreach (int field in new[] { 32, 72, CooldownField })
            {
                var array = data.Array(field, ConditionRowClass, 8);
                for (int i = 0; i < array.Count; i++) Visit(data.Relative(array.Data + i * 8), 0);
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { return null; }
        return result;

        void Visit(int at, int depth)
        {
            if (at < 4 || !data.Has(at, 8) || depth > 8) throw new InvalidDataException("The perk condition is truncated.");
            if (!seen.Add(at)) return;
            result.Add(data.U8(at + 7));
            uint type = data.U32(at - 4);
            if (type == CounterClass && data.Has(at, 56))
            {
                var rows = data.Array(at + 8, CounterRowClass, 32);
                for (int i = 0; i < rows.Count; i++) Visit(data.Relative(rows.Data + i * 32), depth + 1);
            }
            if (type == GatedClass && data.Has(at, 264)) Visit(data.Relative(at + 256), depth + 1);
            if (type == AllGroupsClass && data.Has(at, 24))
            {
                var groups = data.Array(at + 8, GroupClass, 32);
                for (int i = 0; i < groups.Count; i++)
                {
                    var members = data.Array(groups.Data + i * 32 + 8, ConditionRowClass, 8);
                    for (int j = 0; j < members.Count; j++) Visit(data.Relative(members.Data + j * 8), depth + 1);
                }
            }
        }
    }

    private static IEnumerable<PerkBehaviorNode> Flatten(PerkBehaviorNode node) =>
        node.Children.SelectMany(Flatten).Prepend(node);

    /** The native no-stack sentinel prevents reads from absent stack branches. */
    private static byte[] NewRoot()
    {
        byte[] bytes = new byte[RootSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), InvestmentItemClone.EmptyStringHash);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(128), InvestmentItemClone.EmptyStringHash);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(152), 1);
        bytes.AsSpan(188, 16).Fill(byte.MaxValue);
        return bytes;
    }

    private static void RequireRoot(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length < RootSize || BinaryPrimitives.ReadUInt64LittleEndian(bytes.Span) != (ulong)bytes.Length)
            throw new InvalidDataException("The native perk root is truncated or has an invalid size.");
    }

    private static double Value(PerkBehaviorNode node, string key) =>
        node.Properties.Single(property => property.Key == key).Value;

    private static PerkBehaviorProperty Property(string key, string label, double value, double minimum,
        double maximum, PerkBehaviorValueKind kind = PerkBehaviorValueKind.Float, string description = "") =>
        new() { Key = key, Label = label, Value = value, Minimum = minimum, Maximum = maximum,
            Kind = kind, Description = description };
}
