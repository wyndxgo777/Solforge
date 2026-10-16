using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Formats;

/** Native node decoding and encoding. */
public static partial class NativePerkBehavior
{
    /** Reads one payload; a payload the decoders refuse stays a preserved native node. */
    private static PerkBehaviorNode ReadNode(ReadOnlyMemory<byte> bytes, int at, PerkBehaviorNodeKind kind,
        Role role, string id, List<string> diagnostics)
    {
        var data = new TagReader(bytes);
        if (at < 4 || !data.Has(at, kind == PerkBehaviorNodeKind.Condition ? 8 : 1))
            throw new InvalidDataException("The perk node is truncated.");
        uint type = data.U32(at - 4);
        var node = TryReadNode(bytes, at, type, kind, role, id, diagnostics);
        if (node is null)
        {
            node = new() { Primitive = "native", Title = $"Native operation {type:X8}",
                Kind = kind, NativeClass = type, Editable = false,
                Description = "This operation is preserved. Its implementation and undecoded inputs are read-only." };
            diagnostics.Add($"Native operation {type:X8} has inputs this editor preserves without editing.");
            if (kind == PerkBehaviorNodeKind.Condition && data.U8(at + 4) == byte.MaxValue
                && F32(bytes, at) is >= 0 and <= 1)
            {
                node.Primitive = "native-condition";
                node.Title = $"Native condition {data.U8(at + 5)}";
                node.Editable = true;
                node.Properties.Add(Property("probability", "Probability", F32(bytes, at), 0, 1));
                node.Description = "Probability is editable. Native condition logic and its other inputs are preserved.";
            }
        }
        node.Id = id;
        node.OriginalOffset = at;
        node.NativeBytes = bytes.Span.Slice(at, Math.Min(8, bytes.Length - at)).ToArray();
        node.LabelFilters.Clear();
        if (node.Primitive != "native" && data.Has(at, LabelSitesOf(type).Select(site => site.Kind + 16).DefaultIfEmpty(0).Max()))
        {
            try { ReadLabelFilters(bytes, at, node); }
            catch (InvalidDataException) { node.LabelFilters.Clear(); }
        }
        return node;
    }

    /** Editable inputs require both the node class and the native dispatch opcode to agree. */
    private static PerkBehaviorNode? TryReadNode(ReadOnlyMemory<byte> bytes, int at, uint type,
        PerkBehaviorNodeKind kind, Role role, string id, List<string> diagnostics)
    {
        var data = new TagReader(bytes);
        string? key = kind == PerkBehaviorNodeKind.Condition ? ConditionKey(data, at, type, role) : EffectKey(data, at, type);
        if (key is null) return null;
        var node = Blank(key, Primitives.First(row => row.Key == (key == "state" ? "state-kill" : key)));
        var properties = node.Properties;
        void Set(string property, double value) => properties.First(row => row.Key == property).Value = value;
        if (kind == PerkBehaviorNodeKind.Condition) Set("probability", F32(bytes, at));
        if (FieldsOf(key) is { } fields && !ReadFields(bytes, at, fields, node)) return null;
        switch (key)
        {
            case "timer":
                Set("seconds", F32(bytes, at + 8));
                break;
            case "property":
                ReadProperty(bytes, at, node);
                break;
            case "ammo-drop":
                for (int i = 0; i < properties.Count; i++) properties[i].Value = F32(bytes, at + 12 + i * 4);
                break;
            case "while-state" or "state":
                byte selector = data.U8(at + 128), bits = data.U8(at + 129);
                if (selector == bits && selector <= 1) Set("this_weapon_active", selector);
                else
                {
                    properties.RemoveAll(row => row.Key == "this_weapon_active");
                    properties.Add(Info("slot_test", "Slot test (selector, bits)", selector | bits << 8,
                        PerkBehaviorValueKind.Integer));
                }
                if (OtherStateTests(bytes, at)) properties.Add(Info("other_tests", "Other native tests (kept as is)", 1));
                break;
            case "kill" or "damage" or "unequip":
                if (OtherFilters(data, at, key)) properties.Add(Info("other_filters", "Other native filters (kept as is)", 1));
                break;
            case "ability-energy" or "driven-spawn":
                if (!ReadExpression(bytes, at + (key == "ability-energy" ? 24 : 32), node)) return null;
                if (key == "ability-energy" && data.U8(at + 4) != 0)
                    properties.Add(Info("slot_table", "Slot table", data.U8(at + 4), PerkBehaviorValueKind.Integer));
                break;
            case "damage-multiplier":
                ReadDamageMultiplier(bytes, at, node);
                break;
        }
        if (key == "state")
        {
            int innerAt = Target(data, at + 256);
            var inner = ReadNode(bytes, innerAt, PerkBehaviorNodeKind.Condition, Role.Inner, id + "/0", diagnostics);
            node.Children.Add(inner);
            if (PrimitiveOf(StatePrefix + inner.Primitive) is { } spec)
                (node.Primitive, node.Title, node.Description) = (spec.Key, spec.Name, spec.Description);
            else if (inner.Primitive is not ("native" or "native-condition"))
            {
                node.Primitive = StatePrefix + inner.Primitive;
                node.Title = inner.Title + " while a state holds";
                node.Description = "The state test and the inner condition are editable. Both probabilities apply.";
            }
            else
            {
                node.Primitive = StatePrefix + "native";
                node.Title = "On a native event while a state holds";
                node.Description = "The state test is editable. The inner condition is preserved.";
            }
        }
        if (key == "counter" && !ReadRows(bytes, at, node, id, diagnostics)) return null;
        if (SpecOf(key) is { } table && !ReadSpec(bytes, at, table, node, id, diagnostics)) return null;
        foreach (var property in properties)
        {
            property.Minimum = Math.Min(property.Minimum, property.Value);
            property.Maximum = Math.Max(property.Maximum, property.Value);
        }
        return node;
    }

    /** Event targets, restore mode 1, child masks and expressions other than a constant are kept as installed. */
    private static void ReadProperty(ReadOnlyMemory<byte> bytes, int at, PerkBehaviorNode node)
    {
        var data = new TagReader(bytes);
        var properties = node.Properties;
        PerkBehaviorProperty Get(string key) => properties.First(row => row.Key == key);
        var target = Get("target");
        target.Value = data.U8(at + 2);
        target.Editable = target.Value <= 1;
        Get("hash").Value = data.U32(at + 8);
        Get("operation").Value = data.U8(at + 73);
        byte restore = data.U8(at + 74);
        if (restore is 0 or 2) Get("restore").Value = restore == 2 ? 1 : 0;
        else
        {
            properties.Remove(Get("restore"));
            properties.Add(Info("restore_mode", "Restore mode (kept as is)", restore, PerkBehaviorValueKind.Integer));
        }
        int constant = ConstantLocation(bytes, at + 24);
        if (constant >= 0) Get("value").Value = F32(bytes, constant);
        else
        {
            properties.Remove(Get("value"));
            properties.Add(Info("value_expression", "Value: input expression (kept as is)", 1));
        }
        if (data.U8(at + 3) != 1 || data.U8(at + 4) != 0 || data.U8(at + 72) != 0)
            properties.Add(Info("other", "Other native settings (kept as is)", 1));
    }

    /** Header byte +6 must equal the state-record rule, or the node stays native. */
    private static string? ConditionKey(TagReader data, int at, uint type, Role role)
    {
        if (data.U8(at + 4) != byte.MaxValue) return null;
        byte opcode = data.U8(at + 5), owner = data.U8(at + 6);
        string? key = type switch
        {
            AlwaysClass when opcode == 0 => "always",
            TimerClass when opcode == 1 && data.Has(at, 12) => "timer",
            EventClass when opcode == 30 && data.Has(at, 12) => "event",
            UnequipClass when opcode == 17 && data.Has(at, 112) => "unequip",
            KillClass when opcode == 2 && data.Has(at, 344) => "kill",
            DamageClass when opcode == 4 && data.Has(at, 208) => "damage",
            StateClass when opcode == 20 && data.Has(at, 256) => "while-state",
            GatedClass when opcode == 35 && data.Has(at, 264) && data.U32(at + 224) == 0
                && Target(data, at + 256) >= 4 => "state",
            CounterClass when opcode == 26 && data.Has(at, 56)
                && data.TryArray(at + 8, CounterRowClass, 32, out _) => "counter",
            _ => SpecOf(PerkBehaviorNodeKind.Condition, type, opcode) is { } spec && data.Has(at, spec.Size) ? spec.Key : null
        };
        if (key is null) return null;
        bool owns = role is not (Role.Inner or Role.Op32) && key switch
        {
            "while-state" or "state" => data.U32(at + 212) != EmptyHash,
            "damage" => data.I64(at + 200) != 0,
            _ => false
        };
        return owner == (owns ? 1 : 0) ? key : null;
    }

    private static string? EffectKey(TagReader data, int at, uint type) => type switch
    {
        PropertyClass when data.Has(at, 80) && data.U8(at) == 10 && data.U8(at + 1) == 1 && data.U8(at + 2) <= 3
            && data.U8(at + 73) <= 3 => "property",
        AmmoDropClass when data.Has(at, 48) && data.U8(at) == 11 && data.U8(at + 1) == 0
            && data.U32(at + 4) == 0 && data.U32(at + 8) == 0 => "ammo-drop",
        SpawnClass when data.Has(at, 64) && data.U8(at) == 1 && data.U8(at + 1) == 1 && data.U8(at + 2) <= 3 => "spawn",
        DrivenSpawnClass when data.Has(at, 88) && data.U8(at) == 2 && data.U8(at + 1) == 1
            && data.U8(at + 2) <= 3 => "driven-spawn",
        EnergyClass when data.Has(at, 80) && data.U8(at) == 8 && data.U8(at + 1) == 0
            && data.U8(at + 2) <= 7 && data.U8(at + 3) <= 2 => "ability-energy",
        DamageScaleClass when data.Has(at, 336) && (data.U64(at) & ~0xFFUL) == 0 && data.U8(at) == 40 => "damage-multiplier",
        CounterEffectClass when data.Has(at, 8) && data.U8(at) == 42 && data.U8(at + 1) == 0
            && data.U8(at + 2) <= 1 && data.U8(at + 3) == 0 => "perk-counter",
        TransmatClass when data.Has(at, 8) && data.U32(at) == 47 => "transmat",
        _ => SpecOf(PerkBehaviorNodeKind.Effect, type, data.U8(at)) is { } spec && data.Has(at, spec.Size)
            && (spec.Size < 2 || data.U8(at + 1) == StateFlag(spec.Opcode)) ? spec.Key : null
    };

    /** Rows keep their native order; a row's own inputs join its condition's inputs. */
    private static bool ReadRows(ReadOnlyMemory<byte> bytes, int at, PerkBehaviorNode node, string id, List<string> diagnostics)
    {
        var data = new TagReader(bytes);
        var rows = data.Array(at + 8, CounterRowClass, 32);
        for (int i = 0; i < rows.Count; i++)
        {
            int row = rows.Data + i * 32, childAt = Target(data, row);
            if (childAt < 4) return false;
            var child = ReadNode(bytes, childAt, PerkBehaviorNodeKind.Condition, Role.Row, $"{id}/{i}", diagnostics);
            child.Editable = true;
            var inputs = RowInputs();
            child.Properties.AddRange(inputs);
            if (!ReadFields(bytes, row, Fields["row"], child)) return false;
            if (data.U8(row + 8) > 2) inputs[0].Editable = false;
            if (data.U8(row + 9) != 0 || data.U32(row + 20) != 0 || data.U32(row + 24) != 0 || data.U32(row + 28) != 0
                || data.U16(row + 10) != 0)
                child.Properties.Add(Info("row_extra", "Row: other settings (kept as is)", 1));
            foreach (var property in inputs)
            {
                property.Minimum = Math.Min(property.Minimum, property.Value);
                property.Maximum = Math.Max(property.Maximum, property.Value);
            }
            node.Children.Add(child);
        }
        return true;
    }

    private static void ReadDamageMultiplier(ReadOnlyMemory<byte> bytes, int at, PerkBehaviorNode node)
    {
        var data = new TagReader(bytes);
        int child = Target(data, at + 320);
        uint type = child >= 4 && data.Has(child, 8) ? data.U32(child - 4) : 0;
        if (type == BonusClass)
        {
            node.Properties.First(row => row.Key == "bonus").Value = F32(bytes, child);
            node.Properties.First(row => row.Key == "player_bonus").Value = F32(bytes, child + 4);
        }
        else
        {
            node.Properties.RemoveAll(row => row.Key is "bonus" or "player_bonus");
            node.Properties.Add(Choices(Info("bonus_source", "Bonus source", type switch
            {
                0 => 0, BonusExpressionClass => 2, BonusOwnerClass => 3, _ => 4
            }, PerkBehaviorValueKind.Integer), (0, "None"), (2, "Input expression"), (3, "Owner value"), (4, "Other")));
        }
        if (!AllFiltersPass(data, at)) node.Properties.Add(Info("filtered", "Other native filters (kept as is)", 1));
        foreach (var (field, name) in new[] { (288, "Set"), (304, "Scale") })
        {
            if (!data.TryArray(at + field, DamageRowClass, 12, out var rows)) continue;
            for (int i = 0; i < rows.Count; i++)
                node.Properties.Add(Info($"{name.ToLowerInvariant()}_{i}",
                    $"{name} damage value {i + 1} (index {(sbyte)data.U8(rows.Data + i * 12)})",
                    F32(bytes, rows.Data + i * 12 + 4), PerkBehaviorValueKind.Float));
        }
    }

    /** Every filter other than the editable label filters is empty or off. */
    private static bool AllFiltersPass(TagReader data, int at) =>
        data.U8(at + 8) == 0x20 && data.U32(at + 156) == 0 && data.U8(at + 177) == 1 && data.U8(at + 328) == 0
        && new[] { 24, 40, 56, 288, 304 }.All(field => data.U64(at + field) == 0);

    /** True when an installed filter other than a label filter differs from the form Studio writes. */
    private static bool OtherFilters(TagReader data, int at, string key) => key switch
    {
        "unequip" => false,
        "kill" => data.U8(at + 312) != 1 || data.U8(at + 313) != 1 || data.U64(at + 168) != 0 || data.U8(at + 322) != 0
            || data.U32(at + 324) != EmptyHash || data.U32(at + 328) != EmptyHash || data.U8(at + 332) != 0
            || data.U32(at + 340) != 0xBF800000 || data.U32(at + 288) != 0 || data.U32(at + 292) != 0,
        _ => data.U8(at + 144) != 1 || data.U8(at + 145) != 1
            || data.U32(at + 156) != EmptyHash || data.U8(at + 165) != 0 || data.U8(at + 166) != 0
            || data.U8(at + 167) != 0 || data.U64(at + 176) != 0 || data.U32(at + 192) != 0
            || data.I64(at + 200) != 0 || data.U32(at + 120) != 0 || data.U32(at + 124) != 0
    };

    private static bool OtherStateTests(ReadOnlyMemory<byte> bytes, int at)
    {
        var span = bytes.Span.Slice(at, 256);
        var template = StateTemplate();
        return !span[8..128].SequenceEqual(template.AsSpan(8, 120)) || !span[130..212].SequenceEqual(template.AsSpan(130, 82))
            || !span[228..248].SequenceEqual(template.AsSpan(228, 20)) || !span[249..256].SequenceEqual(template.AsSpan(249, 7));
    }

    /** Native defaults of the while-state body: every range is open and every optional test is off. */
    private static byte[] StateTemplate()
    {
        byte[] bytes = new byte[256];
        void Float(int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);
        foreach (int at in new[] { 12, 28, 36, 44, 136, 144, 152, 160, 168, 176, 184, 216, 220 }) Float(at, 1);
        Float(16, -1); Float(20, -1); Float(52, 32);
        // The relation filter's +0 is 0x20 in every installed node.
        bytes[64] = 0x20;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(200), NoTag);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(212), EmptyHash);
        return bytes;
    }

    /** Only the constant and pass-through forms are editable; other programs stay read-only. */
    private static bool ReadExpression(ReadOnlyMemory<byte> bytes, int at, PerkBehaviorNode node)
    {
        var (form, constant) = Expression(bytes, at);
        var expression = node.Properties.First(row => row.Key == "expression");
        expression.Value = form;
        if (form == 2)
        {
            expression.Editable = false;
            expression.Choices.Add(new(2, "Other (read-only)"));
            node.Properties.RemoveAll(row => row.Key == "constant");
        }
        else node.Properties.First(row => row.Key == "constant").Value = constant;
        return true;
    }

    /** Returns 0 for a constant, 1 for pass-through, 2 for any other expression program. */
    private static (int Form, float Constant) Expression(ReadOnlyMemory<byte> bytes, int at)
    {
        var data = new TagReader(bytes);
        if (!data.Has(at - 8, 56) || data.U64(at - 8) != 0 || data.U64(at + 32) != 1 || data.U64(at + 40) != 1
            || !data.TryArray(at, CodeClass, 1, out var code) || !data.TryArray(at + 16, ConstantClass, 16, out var constants)
            || code.Count != 4) return (2, 0);
        var program = bytes.Span.Slice(code.Data, 4);
        if (program.SequenceEqual(PassCode) && constants.Count == 0) return (1, 1);
        if (!program.SequenceEqual(ConstantCode) || constants.Count != 1) return (2, 0);
        uint lane = data.U32(constants.Data);
        float scalar = BitConverter.UInt32BitsToSingle(lane);
        if (!float.IsFinite(scalar) || Enumerable.Range(1, 3).Any(i => data.U32(constants.Data + i * 4) != lane)) return (2, 0);
        return (0, scalar);
    }

    /** Booleans must be 0 or 1 and floats finite, or the node stays native. */
    private static bool ReadFields(ReadOnlyMemory<byte> bytes, int at, Field[] fields, PerkBehaviorNode node)
    {
        var data = new TagReader(bytes);
        foreach (var field in fields)
        {
            var property = node.Properties.FirstOrDefault(row => row.Key == field.Key);
            if (property is null) continue;
            // Each arm is widened to double; a float arm would otherwise round the u32 hashes.
            double value = field.Width switch
            {
                Width.U8 => (double)data.U8(at + field.Offset),
                Width.I8 => (double)(sbyte)data.U8(at + field.Offset),
                Width.U32 => (double)data.U32(at + field.Offset),
                Width.I32 => (double)data.I32(at + field.Offset),
                _ => (double)F32(bytes, at + field.Offset)
            };
            if (!double.IsFinite(value) || property.Kind == PerkBehaviorValueKind.Boolean && value > 1) return false;
            property.Value = value;
        }
        return true;
    }

    private static void WriteFields(PerkWriter writer, int at, Field[] fields, PerkBehaviorNode node)
    {
        foreach (var field in fields)
        {
            var property = node.Properties.FirstOrDefault(row => row.Key == field.Key);
            if (property is null || !property.Editable) continue;
            switch (field.Width)
            {
                case Width.U8: writer.U8(at + field.Offset, checked((byte)property.Value)); break;
                case Width.I8: writer.U8(at + field.Offset, unchecked((byte)checked((sbyte)property.Value))); break;
                case Width.U32: writer.U32(at + field.Offset, checked((uint)property.Value)); break;
                case Width.I32: writer.U32(at + field.Offset, unchecked((uint)checked((int)property.Value))); break;
                default: writer.F32(at + field.Offset, (float)property.Value); break;
            }
        }
    }

    /** Existing nodes keep their payload; edited constants receive private storage. */
    private static void PatchNode(PerkWriter writer, ReadOnlyMemory<byte> original,
        PerkBehaviorNode node, int at, Role role)
    {
        if (node.Primitive == "native") return;
        string shape = Shape(node.Primitive);
        if (shape == GroupKey) return;
        if (node.Kind == PerkBehaviorNodeKind.Condition) writer.F32(at, (float)Value(node, "probability"));
        if (FieldsOf(shape) is { } fields) WriteFields(writer, at, fields, node);
        if (SpecOf(shape) is { } spec) PatchSpec(writer, original, node, at, spec);
        switch (shape)
        {
            case "timer":
                writer.F32(at + 8, (float)Value(node, "seconds"));
                break;
            case "property":
                writer.U8(at + 2, (byte)Value(node, "target")); writer.U32(at + 8, (uint)Value(node, "hash"));
                writer.U8(at + 73, (byte)Value(node, "operation"));
                if (node.Properties.Any(row => row.Key == "restore"))
                    writer.U8(at + 74, Value(node, "restore") == 0 ? (byte)0 : (byte)2);
                if (node.Properties.All(row => row.Key != "value")) break;
                int constant = ConstantLocation(original, at + 24);
                float value = (float)Value(node, "value");
                if (BitConverter.SingleToInt32Bits(F32(original, constant)) != BitConverter.SingleToInt32Bits(value))
                {
                    constant = writer.Array(at + 40, ConstantClass, 1, 16);
                    for (int lane = 0; lane < 4; lane++) writer.F32(constant + lane * 4, value);
                }
                break;
            case "ammo-drop":
                WriteAmmoDrop(writer, node, at);
                break;
            case "while-state" or "state":
                WriteStateExtras(writer, node, at, role);
                break;
            case "ability-energy" or "driven-spawn":
                PatchExpression(writer, original, node, at + (shape == "ability-energy" ? 24 : 32));
                break;
            case "damage-multiplier" when node.Properties.Any(row => row.Key == "bonus"):
                int child = Target(new TagReader(original), at + 320);
                writer.F32(child, (float)Value(node, "bonus"));
                writer.F32(child + 4, (float)Value(node, "player_bonus"));
                break;
            case "counter":
                var rows = new TagReader(original).Array(at + 8, CounterRowClass, 32);
                for (int i = 0; i < rows.Count; i++)
                {
                    WriteFields(writer, rows.Data + i * 32, Fields["row"], node.Children[i]);
                    PatchNode(writer, original, node.Children[i], node.Children[i].OriginalOffset, Role.Row);
                }
                break;
        }
        if (shape == "state") PatchNode(writer, original, node.Children[0], node.Children[0].OriginalOffset, Role.Inner);
        WriteLabelFilters(writer, original, node, at);
    }

    /** Fresh payloads use native defaults for every input the node does not expose. */
    private static int WriteNode(PerkWriter writer, PerkBehaviorNode node, Role role, ref int index)
    {
        string shape = Shape(node.Primitive);
        int size = shape switch
        {
            "always" or "perk-counter" or "transmat" => 8, "timer" or "event" => 12, "unequip" => 112,
            "kill" => 344, "damage" => 208, "while-state" => 256, "state" => 264, "counter" => 56,
            "property" => 80, "ammo-drop" => 48, "spawn" => 64, "driven-spawn" => 88, "ability-energy" => 80,
            "damage-multiplier" => 336,
            _ => SpecOf(shape)?.Size ?? throw new InvalidDataException("This native node has no writer.")
        };
        int at = writer.Node(node.NativeClass, size);
        int opcode = PrimitiveOf(node.Primitive)!.Opcode;
        if (node.Kind == PerkBehaviorNodeKind.Condition)
        {
            writer.F32(at, (float)Value(node, "probability"));
            writer.U8(at + 4, byte.MaxValue);
            writer.U8(at + 5, checked((byte)opcode));
            // Extend-timers conditions take no index; every other condition takes the next free one.
            writer.U8(at + 7, role == Role.Op32 ? byte.MaxValue : checked((byte)index++));
        }
        else
        {
            writer.U8(at, checked((byte)opcode));
            if (size > 1) writer.U8(at + 1, StateFlag(opcode));
        }
        switch (shape)
        {
            case "timer":
                writer.F32(at + 8, (float)Value(node, "seconds"));
                break;
            case "property":
                writer.U8(at + 1, 1);
                writer.U8(at + 2, (byte)Value(node, "target")); writer.U8(at + 3, 1);
                writer.U32(at + 8, (uint)Value(node, "hash"));
                int code = writer.Array(at + 24, CodeClass, 4, 1);
                writer.Copy(code, ConstantCode);
                int constant = writer.Array(at + 40, ConstantClass, 1, 16);
                for (int lane = 0; lane < 4; lane++) writer.F32(constant + lane * 4, (float)Value(node, "value"));
                writer.U64(at + 56, 1); writer.U64(at + 64, 1);
                writer.U8(at + 73, (byte)Value(node, "operation"));
                writer.U8(at + 74, Value(node, "restore") == 0 ? (byte)0 : (byte)2);
                break;
            case "ammo-drop":
                WriteAmmoDrop(writer, node, at);
                break;
            case "unequip":
                writer.U32(at + 88, NoTag); writer.U8(at + 96, byte.MaxValue);
                break;
            case "kill":
                writer.U32(at + 80, NoTag); writer.U8(at + 88, byte.MaxValue);
                writer.U32(at + 280, NoTag); writer.U8(at + 296, byte.MaxValue);
                // Any damage type, and skip the exclude mask; the zero masks alone never pass.
                writer.U8(at + 312, 1); writer.U8(at + 313, 1);
                writer.U32(at + 324, EmptyHash); writer.U32(at + 328, EmptyHash); writer.F32(at + 340, -1);
                break;
            case "damage":
                writer.U32(at + 112, NoTag); writer.U8(at + 128, byte.MaxValue);
                writer.U8(at + 144, 1); writer.U8(at + 145, 1); writer.U32(at + 156, EmptyHash);
                break;
            case "while-state" or "state":
                writer.Copy(at + 8, StateTemplate()[8..]);
                break;
            case "spawn" or "driven-spawn":
                // Spawning effects keep a state slot for the spawned object.
                writer.U8(at + 1, 1);
                break;
            case "damage-multiplier":
                writer.U8(at + 8, 0x20);
                int path = writer.String(LabelGlobalsPath);
                writer.I64(at + 136, path - at - 136); writer.U32(at + 144, LabelGlobalsTag);
                writer.I64(at + 256, path - at - 256); writer.U32(at + 264, LabelGlobalsTag);
                writer.U8(at + 160, byte.MaxValue); writer.U8(at + 177, 1); writer.U8(at + 272, byte.MaxValue);
                int bonus = writer.Node(BonusClass, 8);
                writer.F32(bonus, (float)Value(node, "bonus")); writer.F32(bonus + 4, (float)Value(node, "player_bonus"));
                writer.I64(at + 320, bonus - at - 320);
                break;
        }
        var spec = SpecOf(shape);
        if (spec is not null) WriteSpecDefaults(writer, at, spec);
        if (FieldsOf(shape) is { } fields) WriteFields(writer, at, fields, node);
        if (spec is not null) WriteSpecParts(writer, node, at, spec, ref index);
        if (shape is "while-state" or "state") WriteStateExtras(writer, node, at, role);
        if (shape is "ability-energy" or "driven-spawn")
        {
            int expression = at + (shape == "ability-energy" ? 24 : 32);
            WriteExpression(writer, expression, (int)Value(node, "expression"), (float)Value(node, "constant"));
            writer.U64(expression + 32, 1); writer.U64(expression + 40, 1);
        }
        if (shape == "state")
        {
            int inner = WriteNode(writer, node.Children[0], Role.Inner, ref index);
            writer.I64(at + 256, inner - at - 256);
        }
        if (shape == "counter")
        {
            int rows = writer.Array(at + 8, CounterRowClass, node.Children.Count, 32);
            for (int i = 0; i < node.Children.Count; i++)
            {
                int row = rows + i * 32;
                int child = WriteNode(writer, node.Children[i], Role.Row, ref index);
                writer.I64(row, child - row);
                WriteFields(writer, row, Fields["row"], node.Children[i]);
            }
            writer.U64(at + 24, node.Children.Aggregate(0UL, (mask, child) => mask | Bits(child)));
        }
        WriteLabelFilters(writer, null, node, at);
        return at;
    }

    /** Byte +6 marks a condition that owns a state record; an inner condition never does. */
    private static void WriteStateExtras(PerkWriter writer, PerkBehaviorNode node, int at, Role role)
    {
        bool owns = role is not (Role.Inner or Role.Op32) && (uint)Value(node, "property") != EmptyHash;
        writer.U8(at + 6, owns ? (byte)1 : (byte)0);
        if (node.Properties.FirstOrDefault(row => row.Key == "this_weapon_active") is { } active)
        {
            // Selector 1 is this perk's slot; bit 0 requires that slot's weapon to be active.
            writer.U8(at + 128, (byte)active.Value);
            writer.U8(at + 129, (byte)active.Value);
        }
    }

    /** New programs are appended, so a shared program is never changed in place. */
    private static void WriteExpression(PerkWriter writer, int at, int form, float constant)
    {
        int code = writer.Array(at, CodeClass, 4, 1);
        writer.Copy(code, form == 0 ? ConstantCode : PassCode);
        int values = writer.Array(at + 16, ConstantClass, form == 0 ? 1 : 0, 16);
        if (form == 0)
            for (int lane = 0; lane < 4; lane++) writer.F32(values + lane * 4, constant);
    }

    private static void WriteAmmoDrop(PerkWriter writer, PerkBehaviorNode node, int at)
    {
        writer.U8(at, 11);
        var expected = Inputs("ammo-drop", PerkBehaviorNodeKind.Effect);
        for (int i = 0; i < expected.Count; i++) writer.F32(at + 12 + i * 4, (float)Value(node, expected[i].Key));
    }

    /** Native evaluator overrides and multi-instruction expressions remain read-only here. */
    private static int ConstantLocation(ReadOnlyMemory<byte> bytes, int expression)
    {
        var data = new TagReader(bytes);
        if (!data.Has(expression, 48) || data.U32(expression + 44) != 0) return -1;
        var code = data.Array(expression, CodeClass, 1);
        var constants = data.Array(expression + 16, ConstantClass, 16);
        if (code.Count != 4 || constants.Count != 1
            || !bytes.Span.Slice(code.Data, 4).SequenceEqual(ConstantCode)) return -1;
        float scalar = F32(bytes, constants.Data);
        if (!float.IsFinite(scalar)) return -1;
        for (int lane = 1; lane < 4; lane++)
            if (F32(bytes, constants.Data + lane * 4) != scalar) return -1;
        return constants.Data;
    }

    private static PerkBehaviorProperty Info(string key, string label, double value,
        PerkBehaviorValueKind kind = PerkBehaviorValueKind.Boolean)
    {
        var property = Property(key, label, value, value, value, kind, "Read only; kept as installed.");
        property.Editable = false;
        return property;
    }

    /** A relative pointer's target, or -1 when it is empty or leaves the payload. */
    private static int Target(TagReader data, int field)
    {
        try { return data.Relative(field); }
        catch (InvalidDataException) { return -1; }
    }

    private static float F32(ReadOnlyMemory<byte> bytes, int at) =>
        BinaryPrimitives.ReadSingleLittleEndian(bytes.Span[at..]);

    private static byte[] Ascii(string text) => [.. Encoding.ASCII.GetBytes(text), 0];
}
