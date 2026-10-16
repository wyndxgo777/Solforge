using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** Node specs for conditions and effects that use label values. */
public static partial class NativePerkBehavior
{
    // A label value packs its bit index above the 32-bit label hash.
    private const double LabelBitScale = 4294967296.0;
    // Refill fractions: five expressions, each after an 8-byte zero member.
    private static readonly (string Key, int Offset)[] Fractions =
        [("active_fraction", 120), ("this_fraction", 176), ("slot0_fraction", 232), ("slot1_fraction", 288), ("slot2_fraction", 344)];

    // Movement block class 0x8080692C, 72 bytes: two sets of eight floats; a mode-state flag picks the set.
    private const uint MovementBlockClass = 0x8080692C;
    private const int MovementBlockSize = 72;
    // Component scale names are a u32 array.
    private const uint NameListClass = 0x80800070;

    /** Movement block inputs and their offsets inside the block; a property, so static order never matters. */
    private static (Input Input, int Offset, Width Width)[] MovementInputs =>
    [
        (new("name", "Block name hash", -1, Width.U32, EmptyHash, 0, uint.MaxValue, PerkBehaviorValueKind.Hash,
            "Not read on the path Studio decoded."), 0, Width.U32),
        (Flag("allow_first", "Allowed in state A", -1, 1, "Which state is A is not known."), 4, Width.U8),
        (Flag("allow_second", "Allowed in state B", -1, 1), 5, Width.U8),
        .. new[] { (8, "first", "First set"), (40, "second", "Second set") }.SelectMany(set => new[]
        {
            (Number(set.Item2 + "_value0", set.Item3 + ": value 0", -1, 0, -1000000, 1000000, "Copied into the mode."), set.Item1, Width.F32),
            (Number(set.Item2 + "_entry", set.Item3 + ": entry speed value", -1, -1, -1, 1000000, "-1 turns it off."), set.Item1 + 4, Width.F32),
            (Number(set.Item2 + "_value8", set.Item3 + ": value 8", -1, 0, -1000000, 1000000, "Copied into the mode."), set.Item1 + 8, Width.F32),
            (Number(set.Item2 + "_hmin", set.Item3 + ": horizontal speed minimum", -1, 0, -1000000, 1000000), set.Item1 + 12, Width.F32),
            (Number(set.Item2 + "_hmax", set.Item3 + ": horizontal speed maximum", -1, 0, -1000000, 1000000), set.Item1 + 16, Width.F32),
            (Number(set.Item2 + "_value20", set.Item3 + ": value 20", -1, 0, -1000000, 1000000, "Copied into the mode."), set.Item1 + 20, Width.F32),
            (Number(set.Item2 + "_vmin", set.Item3 + ": vertical speed minimum", -1, 0, -1000000, 1000000), set.Item1 + 24, Width.F32),
            (Number(set.Item2 + "_vmax", set.Item3 + ": vertical speed maximum", -1, 0, -1000000, 1000000), set.Item1 + 28, Width.F32)
        })
    ];

    /** Effect byte +1 is 1 exactly for opcodes whose dispatch slot +32 allocates effect state. */
    private static byte StateFlag(int opcode) =>
        opcode is 0 or 1 or 2 or 6 or 7 or 10 or 17 or 18 or 22 or 25 or 26 or 28 or 29 or 30 or 33 or 35 or 41 or 52
            ? (byte)1 : (byte)0;

    /** Condition kinds that test one event and keep no state, so any list of conditions can hold them. */
    public static IReadOnlyList<string> EventPrimitives =>
        ["kill", "damage", "unequip", "event", .. SpecTableKeys(PerkBehaviorNodeKind.Condition).Where(key => key != "all-groups")];

    /** Condition kinds a new group of an all-groups condition can hold. */
    public static IReadOnlyList<string> GroupPrimitives => ["while-state", .. StateGatedKeys, .. EventPrimitives];

    /** State-gated conditions not already in Primitives; the palette lists them apart from the main kinds. */
    public static IReadOnlyList<PerkBehaviorPrimitive> StateGatedPrimitives => stateGated ??=
        [.. StateGatedKeys.Where(key => Primitives.All(row => row.Key != key)).Select(key => PrimitiveOf(key)!)];
    private static IReadOnlyList<PerkBehaviorPrimitive>? stateGated;

    private static IEnumerable<string> StateGatedKeys => EventPrimitives.Select(inner => StatePrefix + inner);

    /** A palette kind by key, including the state-gated form of each event condition. */
    public static PerkBehaviorPrimitive? PrimitiveOf(string key)
    {
        if (Primitives.FirstOrDefault(row => row.Key == key) is { } listed) return listed;
        if (!key.StartsWith(StatePrefix, StringComparison.Ordinal)) return null;
        var inner = Primitives.FirstOrDefault(row => row.Key == key[StatePrefix.Length..]);
        if (inner is null || !EventPrimitives.Contains(inner.Key)) return null;
        return new(key, inner.Name + " while a state holds", PerkBehaviorNodeKind.Condition, GatedClass,
            "Passes when the inner condition passes while an object property is in range. Both probabilities apply.",
            true, 35);
    }

    private static IEnumerable<string> SpecTableKeys(PerkBehaviorNodeKind kind) =>
        SpecTable.All.Where(spec => spec.Kind == kind && spec.CanCreate).Select(spec => spec.Key);

    /** Kinds a new child of this node can be; empty when the node takes no new children. */
    public static IReadOnlyList<string> ChildPrimitives(PerkBehaviorNode parent) =>
        parent.OriginalOffset >= 0 ? [] : parent.Primitive switch
        {
            "counter" => CounterRowPrimitives,
            "all-groups" => [GroupKey],
            GroupKey => GroupPrimitives,
            "extend-timers" => EventPrimitives,
            _ => []
        };

    /** A new child for this parent: a counter row, a group, or a condition. */
    public static PerkBehaviorNode CreateChild(PerkBehaviorNode parent, string primitive)
    {
        if (!ChildPrimitives(parent).Contains(primitive))
            throw new InvalidDataException("This node cannot hold that condition.");
        return parent.Primitive == "counter" ? CreateRow(primitive) : CreateNode(primitive);
    }

    private static PerkBehaviorNode CreateGroup() => new()
    {
        Primitive = GroupKey, Title = "Group", Kind = PerkBehaviorNodeKind.Condition, NativeClass = GroupClass,
        Editable = true, Description = "Passes when any of its conditions passes, or passed within its window.",
        Properties = Inputs(GroupKey, PerkBehaviorNodeKind.Condition)
    };

    private static Role ChildRole(PerkBehaviorNode parent) => parent.NativeClass switch
    {
        CounterClass => Role.Row, GatedClass => Role.Inner, AllGroupsClass => Role.Groups,
        GroupClass => Role.Group, _ => Role.Op32
    };

    /** Decodes the parts a spec's fixed inputs do not cover; false keeps the node native. */
    private static bool ReadSpec(ReadOnlyMemory<byte> bytes, int at, Spec spec, PerkBehaviorNode node, string id,
        List<string> diagnostics)
    {
        var data = new TagReader(bytes);
        try
        {
            foreach (string part in spec.Handler.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] args = part.Split(':');
                switch (args[0])
                {
                    case "labels":
                        ReadLabel(data, at + int.Parse(args[1]), at + int.Parse(args[2]), node);
                        break;
                    case "fractions":
                        foreach (var (key, offset) in Fractions)
                        {
                            var (form, constant) = Expression(bytes, at + offset);
                            var property = node.Properties.First(row => row.Key == key);
                            if (form == 0) property.Value = constant;
                            else
                            {
                                node.Properties.Remove(property);
                                node.Properties.Add(Info(key + "_expression", property.Label + ": input expression (kept as is)", 1));
                            }
                        }
                        break;
                    case "groups":
                        var groups = data.Array(at + 8, GroupClass, 32);
                        for (int i = 0; i < groups.Count; i++)
                        {
                            int row = groups.Data + i * 32;
                            var group = CreateGroup();
                            group.Id = $"{id}/{i}";
                            group.Title = $"Group {i + 1}";
                            group.OriginalOffset = row;
                            group.Properties[0].Value = F32(bytes, row);
                            if (!float.IsFinite(F32(bytes, row))) return false;
                            if (data.U32(row + 4) != 0) group.Properties.Add(Info("other", "Other native settings (kept as is)", 1));
                            ReadList(row + 8, Role.Group, group, group.Id);
                            node.Children.Add(group);
                        }
                        break;
                    case "timer-conditions":
                        ReadList(at + 16, Role.Op32, node, id);
                        break;
                    case "names":
                        var names = data.Array(at + int.Parse(args[1]), NameListClass, 4);
                        var name = node.Properties.First(row => row.Key == "name");
                        if (names.Count == 1) name.Value = data.U32(names.Data);
                        else
                        {
                            node.Properties.Remove(name);
                            node.Properties.Add(Info("names", "Component names (kept as is)", names.Count, PerkBehaviorValueKind.Integer));
                        }
                        break;
                    case "expression":
                        ReadExpression(bytes, at + int.Parse(args[1]), node);
                        break;
                    case "movement":
                        int block = Target(data, at + 8);
                        if (block >= 4 && data.Has(block, MovementBlockSize) && data.U32(block - 4) == MovementBlockClass)
                        {
                            foreach (var (input, offset, width) in MovementInputs)
                            {
                                double value = width switch
                                {
                                    Width.U8 => (double)data.U8(block + offset), Width.U32 => (double)data.U32(block + offset),
                                    _ => (double)F32(bytes, block + offset)
                                };
                                if (!double.IsFinite(value) || input.Kind == PerkBehaviorValueKind.Boolean && value > 1) return false;
                                node.Properties.First(row => row.Key == input.Key).Value = value;
                            }
                        }
                        else
                        {
                            node.Properties.Clear();
                            node.Properties.Add(Info("block_class", "Settings block class (kept as is)",
                                block >= 4 ? data.U32(block - 4) : 0, PerkBehaviorValueKind.Hash));
                        }
                        break;
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { return false; }
        if (spec.Key == "script" && data.U32(at + 8) != 0)
            node.Properties.Add(Info("unread", "Value at +8 (not read by the game; kept as is)", data.U32(at + 8),
                PerkBehaviorValueKind.Integer));
        if (OtherSettings(bytes, at, spec)) node.Properties.Add(Info("other", "Other native settings (kept as is)", 1));
        return true;

        void ReadList(int field, Role role, PerkBehaviorNode parent, string prefix)
        {
            var list = data.Array(field, ConditionRowClass, 8);
            for (int j = 0; j < list.Count; j++)
                parent.Children.Add(ReadNode(bytes, data.Relative(list.Data + j * 8), PerkBehaviorNodeKind.Condition, role,
                    $"{prefix}/{j}", diagnostics));
        }
    }

    /** One label row with its one matching bit is editable; any other set is kept as installed. */
    private static void ReadLabel(TagReader data, int rowsField, int maskField, PerkBehaviorNode node)
    {
        var rows = data.Array(rowsField, LabelRowClass, 24);
        var bits = Enumerable.Range(0, 320).Where(bit => (data.U8(maskField + bit / 8) >> (bit % 8) & 1) != 0).ToArray();
        var label = node.Properties.First(row => row.Key == "label");
        if (rows.Count == 1 && bits.Length == 1) label.Value = bits[0] * LabelBitScale + data.U32(rows.Data);
        else
        {
            node.Properties.Remove(label);
            node.Properties.Add(Info("labels", "Labels (kept as is)", rows.Count, PerkBehaviorValueKind.Integer));
        }
    }

    /** True when a byte outside the inputs differs from what a new node of this kind writes. */
    private static bool OtherSettings(ReadOnlyMemory<byte> bytes, int at, Spec spec)
    {
        byte[] template = new byte[spec.Size];
        bool[] skip = new bool[spec.Size];
        foreach (var value in spec.Defaults ?? []) Put(template, value.Offset, value.Width, value.Value);
        Cover(0, spec.Kind == PerkBehaviorNodeKind.Condition ? 8 : 2);
        foreach (var input in spec.Inputs.Where(input => input.Offset >= 0)) Cover(input.Offset, WidthOf(input.Width));
        foreach (var (offset, length) in (spec.Ignore ?? []).Concat(LabelRanges(spec.Class))) Cover(offset, length);
        var span = bytes.Span.Slice(at, spec.Size);
        for (int i = 0; i < spec.Size; i++)
            if (!skip[i] && span[i] != template[i]) return true;
        return false;

        void Cover(int offset, int length)
        {
            for (int i = offset; i < Math.Min(spec.Size, offset + length); i++) skip[i] = true;
        }
    }

    private static int WidthOf(Width width) => width switch { Width.U8 or Width.I8 => 1, _ => 4 };

    private static void Put(byte[] bytes, int at, Width width, double value)
    {
        switch (width)
        {
            case Width.U8 or Width.I8: bytes[at] = unchecked((byte)(long)value); break;
            case Width.F32: BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), (float)value); break;
            default: BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), unchecked((uint)(long)value)); break;
        }
    }

    /** Native defaults and the copied label table reference of a new node. */
    private static void WriteSpecDefaults(PerkWriter writer, int at, Spec spec)
    {
        foreach (var value in spec.Defaults ?? [])
            switch (value.Width)
            {
                case Width.U8 or Width.I8: writer.U8(at + value.Offset, unchecked((byte)(long)value.Value)); break;
                case Width.F32: writer.F32(at + value.Offset, (float)value.Value); break;
                default: writer.U32(at + value.Offset, unchecked((uint)(long)value.Value)); break;
            }
        if (spec.LabelSet < 0) return;
        // A label set's name at +64 and tag at +72 point at the installed label table.
        int path = writer.String(LabelGlobalsPath);
        writer.I64(at + spec.LabelSet + 64, path - at - spec.LabelSet - 64);
        writer.U32(at + spec.LabelSet + 72, LabelGlobalsTag);
    }

    /** Parts of a new node that are not fixed inputs: lists, labels, expressions and children. */
    private static void WriteSpecParts(PerkWriter writer, PerkBehaviorNode node, int at, Spec spec, ref int index)
    {
        foreach (string part in spec.Handler.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] args = part.Split(':');
            switch (args[0])
            {
                case "types":
                    WriteTypeList(writer, at + int.Parse(args[1]), (uint)Value(node, "types"));
                    break;
                case "labels":
                    WriteLabel(writer, at + int.Parse(args[1]), at + int.Parse(args[2]), Value(node, "label"));
                    break;
                case "fractions":
                    foreach (var (key, offset) in Fractions)
                    {
                        WriteExpression(writer, at + offset, 0, (float)Value(node, key));
                        writer.U64(at + offset + 32, 1); writer.U64(at + offset + 40, 1);
                    }
                    break;
                case "selfref":
                    WriteSelfReference(writer, at);
                    break;
                case "groups":
                    int rows = writer.Array(at + 8, GroupClass, node.Children.Count, 32);
                    for (int i = 0; i < node.Children.Count; i++)
                    {
                        var group = node.Children[i];
                        writer.F32(rows + i * 32, (float)Value(group, "window"));
                        WriteList(writer, rows + i * 32 + 8, group.Children, Role.Group, ref index);
                        writer.U64(rows + i * 32 + 24, group.Children.Aggregate(0UL, (mask, child) => mask | Bits(child)));
                    }
                    break;
                case "timer-conditions":
                    WriteList(writer, at + 16, node.Children, Role.Op32, ref index);
                    writer.U64(at + 32, node.Children.Aggregate(0UL, (mask, child) => mask | Bits(child)));
                    break;
                case "names":
                    WriteNames(writer, at + int.Parse(args[1]), (uint)Value(node, "name"));
                    break;
                case "expression":
                    int expression = at + int.Parse(args[1]);
                    WriteExpression(writer, expression, (int)Value(node, "expression"), (float)Value(node, "constant"));
                    writer.U64(expression + 32, 1); writer.U64(expression + 40, 1);
                    break;
                case "movement":
                    int block = writer.Node(MovementBlockClass, MovementBlockSize);
                    writer.U32(block, EmptyHash);
                    WriteMovement(writer, node, block);
                    writer.I64(at + 8, block - at - 8);
                    break;
            }
        }
    }

    private static void WriteNames(PerkWriter writer, int field, uint name)
    {
        int list = writer.Array(field, NameListClass, 1, 4);
        writer.U32(list, name);
    }

    private static void WriteMovement(PerkWriter writer, PerkBehaviorNode node, int block)
    {
        foreach (var (input, offset, width) in MovementInputs)
            if (node.Properties.FirstOrDefault(row => row.Key == input.Key) is { Editable: true } property)
                switch (width)
                {
                    case Width.U8: writer.U8(block + offset, checked((byte)property.Value)); break;
                    case Width.U32: writer.U32(block + offset, checked((uint)property.Value)); break;
                    default: writer.F32(block + offset, (float)property.Value); break;
                }
    }

    /** Constant and pass-through programs are rewritten when changed; any other program is kept. */
    private static void PatchExpression(PerkWriter writer, ReadOnlyMemory<byte> original, PerkBehaviorNode node, int expression)
    {
        var (form, current) = Expression(original, expression);
        if (form == 2 || node.Properties.All(row => row.Key != "constant")) return;
        int wanted = (int)Value(node, "expression");
        float scalar = (float)Value(node, "constant");
        if (wanted != form || wanted == 0 && BitConverter.SingleToInt32Bits(scalar) != BitConverter.SingleToInt32Bits(current))
            WriteExpression(writer, expression, wanted, scalar);
    }

    private static void WriteList(PerkWriter writer, int field, List<PerkBehaviorNode> children, Role role, ref int index)
    {
        int list = writer.Array(field, ConditionRowClass, children.Count, 8);
        for (int i = 0; i < children.Count; i++)
        {
            int child = WriteNode(writer, children[i], role, ref index);
            writer.I64(list + i * 8, child - list - i * 8);
        }
    }

    /** Installed nodes keep every byte the user did not change; changed lists and labels are appended. */
    private static void PatchSpec(PerkWriter writer, ReadOnlyMemory<byte> original, PerkBehaviorNode node, int at, Spec spec)
    {
        var data = new TagReader(original);
        foreach (string part in spec.Handler.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] args = part.Split(':');
            switch (args[0])
            {
                case "types":
                    if (node.Properties.FirstOrDefault(row => row.Key == "types") is { Editable: true } types
                        && (uint)types.Value != data.U32(at + int.Parse(args[2])))
                        WriteTypeList(writer, at + int.Parse(args[1]), (uint)types.Value);
                    break;
                case "labels":
                    if (node.Properties.FirstOrDefault(row => row.Key == "label") is { Editable: true } label)
                    {
                        var before = new PerkBehaviorNode { Properties = [new() { Key = "label" }] };
                        ReadLabel(data, at + int.Parse(args[1]), at + int.Parse(args[2]), before);
                        if (before.Properties[0].Value != label.Value)
                            WriteLabel(writer, at + int.Parse(args[1]), at + int.Parse(args[2]), label.Value);
                    }
                    break;
                case "fractions":
                    foreach (var (key, offset) in Fractions)
                        if (node.Properties.FirstOrDefault(row => row.Key == key) is { Editable: true } fraction
                            && Expression(original, at + offset) is (0, var constant)
                            && BitConverter.SingleToInt32Bits(constant) != BitConverter.SingleToInt32Bits((float)fraction.Value))
                            WriteExpression(writer, at + offset, 0, (float)fraction.Value);
                    break;
                case "selfref":
                    if (writer.ProgramTag != 0) WriteSelfReference(writer, at);
                    break;
                case "groups":
                    var rows = data.Array(at + 8, GroupClass, 32);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var group = node.Children[i];
                        writer.F32(rows.Data + i * 32, (float)Value(group, "window"));
                        foreach (var child in group.Children) PatchNode(writer, original, child, child.OriginalOffset, Role.Group);
                    }
                    break;
                case "timer-conditions":
                    foreach (var child in node.Children) PatchNode(writer, original, child, child.OriginalOffset, Role.Op32);
                    break;
                case "names":
                    var names = data.Array(at + int.Parse(args[1]), NameListClass, 4);
                    if (node.Properties.FirstOrDefault(row => row.Key == "name") is { Editable: true } name
                        && names.Count == 1 && (uint)name.Value != data.U32(names.Data))
                        WriteNames(writer, at + int.Parse(args[1]), (uint)name.Value);
                    break;
                case "expression":
                    PatchExpression(writer, original, node, at + int.Parse(args[1]));
                    break;
                case "movement":
                    if (node.Properties.Any(row => row.Key == "name")) WriteMovement(writer, node, Target(data, at + 8));
                    break;
            }
        }
    }

    /** Damage types are authored as a u8 list and read as a mask; both must agree. */
    private static void WriteTypeList(PerkWriter writer, int field, uint mask)
    {
        var types = Enumerable.Range(0, 32).Where(bit => (mask >> bit & 1) != 0).ToArray();
        int list = writer.Array(field, TypeListClass, types.Length, 1);
        for (int i = 0; i < types.Length; i++) writer.U8(list + i, (byte)types[i]);
    }

    /** One label row naming the hash, and the 320-bit mask with only that hash's table index set. */
    private static void WriteLabel(PerkWriter writer, int rowsField, int maskField, double value)
    {
        uint hash = (uint)(value % LabelBitScale);
        int bit = (int)(value / LabelBitScale);
        int path = writer.String(LabelGlobalsPath);
        int row = writer.Array(rowsField, LabelRowClass, 1, 24);
        writer.U32(row, hash);
        writer.I64(row + 8, path - row - 8);
        writer.U32(row + 16, LabelGlobalsTag);
        for (int i = 0; i < 40; i++) writer.U8(maskField + i, 0);
        writer.U8(maskField + bit / 8, (byte)(1 << bit % 8));
    }

    /** Damage-resist effects name their own program tag and payload offset at +160. */
    private static void WriteSelfReference(PerkWriter writer, int at)
    {
        writer.U32(at + 160, ResistClass);
        writer.U32(at + 164, 0);
        writer.U32(at + 168, writer.ProgramTag);
        writer.U32(at + 172, ResistClass);
        writer.U64(at + 176, (ulong)at);
    }

    /** The program tag from any damage-resist self reference, root or stack branch; 0 when none. */
    private static uint FindProgramTag(ReadOnlyMemory<byte> bytes)
    {
        var data = new TagReader(bytes);
        try
        {
            var effects = new List<int>();
            var main = data.Array(56, EffectRowClass, 8);
            for (int i = 0; i < main.Count; i++) effects.Add(data.Relative(main.Data + i * 8));
            // Stack branch rows are 72 bytes; their effect array is at row +24.
            if (data.U64(StackField) != 0)
            {
                var stacks = data.Array(StackField, 0, 72);
                for (int s = 0; s < stacks.Count; s++)
                {
                    var list = data.Array(stacks.Data + s * 72 + 24, 0, 8);
                    for (int i = 0; i < list.Count; i++) effects.Add(data.Relative(list.Data + i * 8));
                }
            }
            foreach (int at in effects)
                if (at >= 4 && data.Has(at, 184) && data.U32(at - 4) == ResistClass && data.U32(at + 160) == ResistClass)
                    return data.U32(at + 168);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException) { }
        return 0;
    }
}
