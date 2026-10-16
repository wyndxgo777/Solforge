using System.Buffers.Binary;

namespace SunriseStudio.Formats;

/** Kind of value transform a binding applies between its endpoints. */
public enum CuiBindingTransformKind { None = -1, Not = 0, CompareInteger = 1, CompareFloat = 2, CompareHash = 3 }
/** Comparison operator used by the compare transforms. */
public enum CuiComparison { Equal, NotEqual, Less, LessOrEqual, GreaterOrEqual, Greater }
/** One end of a binding: node IDs (runtime and authored) and the property path under that node. */
public sealed record CuiBindingEndpoint(ushort Node, ushort AuthoredNode, IReadOnlyList<uint> Path)
{
    public string PathKey => string.Join('/', Path.Select(value => value.ToString("X")));
}
/** The transform step of a binding (negate or compare against Operand) with where it is stored. */
public sealed record CuiBindingTransform(CuiBindingTransformKind Kind, CuiComparison Comparison = CuiComparison.Equal,
    uint Operand = 0, int Offset = -1, uint ClassHash = 0);
/** A parsed CUI binding row: source endpoint, transform and target endpoint, or Error if it could not be decoded. */
public sealed record CuiBinding(int Index, int Offset, CuiBindingEndpoint Source, CuiBindingEndpoint Target,
    CuiBindingTransform Transform, string Error);

/** A binding reads endpoint A, transforms its value, and writes endpoint B. */
public static class CuiBindings
{
    // Native path compilation reserves sixteen packed selectors, including the terminal selector.
    public const int MaximumPathLength = 16;
    public const int BindingStride = 56;

    /** Reads every binding row of a CUI tag. */
    public static IReadOnlyList<CuiBinding> Read(ReadOnlyMemory<byte> bytes)
    {
        var r = new TagReader(bytes); var rows = r.Array(88, 0x808046D8, BindingStride);
        List<CuiBinding> result = [];
        for (int index = 0; index < rows.Count; index++)
        {
            int at = rows.Data + index * BindingStride;
            var source = new CuiBindingEndpoint(r.U16(at + 2), r.U16(at), []);
            var target = new CuiBindingEndpoint(r.U16(at + 26), r.U16(at + 24), []);
            var transform = new CuiBindingTransform(CuiBindingTransformKind.None);
            string error = "";
            try
            {
                source = Endpoint(r, at); target = Endpoint(r, at + 24);
                if (r.I64(at + 48) != 0) transform = Transform(r, r.Relative(at + 48));
            }
            catch (Exception failure) when (failure is InvalidDataException or OverflowException or NotSupportedException)
            { error = failure.Message; }
            result.Add(new(index, at, source, target, transform, error));
        }
        return result;
    }

    private static CuiBindingEndpoint Endpoint(TagReader r, int at)
    {
        ulong selector = r.U64(at + 16);
        if (selector > uint.MaxValue) throw new NotSupportedException("Binding selector exceeds its native path word.");
        int slot = r.Relative(at + 8);
        var path = slot < 0 ? (Count: 0, Data: 0, Class: 0u) : r.Array(slot, 0x80804949, 4);
        if (path.Count >= MaximumPathLength) throw new InvalidDataException("Binding path exceeds the native selector capacity.");
        uint[] words = new uint[path.Count + 1];
        for (int i = 0; i < path.Count; i++) words[i] = r.U32(path.Data + i * 4);
        words[^1] = (uint)selector;
        return new(r.U16(at + 2), r.U16(at), Array.AsReadOnly(words));
    }

    /** Tagged transform records distinguish inversion, signed integers, floats, and unsigned hashes. */
    private static CuiBindingTransform Transform(TagReader r, int at)
    {
        if (at < 4 || !r.Has(at, 4)) throw new InvalidDataException("Binding transform leaves the resource.");
        uint type = r.U32(at - 4);
        var expected = type switch
        {
            0x808046E0 => CuiBindingTransformKind.Not, 0x808046DC => CuiBindingTransformKind.CompareInteger,
            0x808046DB => CuiBindingTransformKind.CompareFloat, 0x808046DA => CuiBindingTransformKind.CompareHash,
            _ => throw new NotSupportedException($"Binding transform {type:X8} is unsupported.")
        };
        if (r.U8(at) != (byte)expected) throw new InvalidDataException("Binding transform class and operation disagree.");
        if (expected == CuiBindingTransformKind.Not) return new(expected, Offset: at, ClassHash: type);
        if (!r.Has(at, 8) || r.U8(at + 1) > (byte)CuiComparison.Greater)
            throw new InvalidDataException("Binding comparison is truncated or invalid.");
        return new(expected, (CuiComparison)r.U8(at + 1), r.U32(at + 4), at, type);
    }

    /**
     * Points one comparison binding at a new record; the old record stays for other bindings.
     * @param bytes Resource that holds the binding.
     * @param binding Row read from these bytes.
     * @param comparison Applied as value, comparison, operand.
     * @param operand Operand bits: a signed integer, a float or a hash, by transform kind.
     * @return The edited resource.
     */
    public static byte[] EditComparison(ReadOnlyMemory<byte> bytes, CuiBinding binding, CuiComparison comparison, uint operand)
    {
        var rows = Read(bytes);
        if (binding.Index < 0 || binding.Index >= rows.Count || !Same(rows[binding.Index], binding))
            throw new InvalidDataException("The binding changed. Reload the interface.");
        if (binding.Error.Length != 0 || binding.Transform.Kind is CuiBindingTransformKind.None or CuiBindingTransformKind.Not)
            throw new InvalidDataException("Only a comparison binding has an operand.");
        if (!Enum.IsDefined(comparison)) throw new InvalidDataException("Choose a supported comparison.");
        if (comparison == binding.Transform.Comparison && operand == binding.Transform.Operand) return bytes.ToArray();
        // Comparison records are the class word, then kind, comparison, two zero bytes and the operand.
        int record = checked((bytes.Length + 4 + 7) & ~7);
        byte[] output = new byte[checked(record + 8)]; bytes.Span.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(record - 4), binding.Transform.ClassHash);
        output[record] = (byte)binding.Transform.Kind; output[record + 1] = (byte)comparison;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(record + 4), operand);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(binding.Offset + 48), record - binding.Offset - 48);
        BinaryPrimitives.WriteInt64LittleEndian(output, output.Length);
        return output;
    }

    private static bool Same(CuiBinding a, CuiBinding b) => a.Offset == b.Offset && a.Transform == b.Transform
        && a.Error == b.Error
        && a.Source.Node == b.Source.Node && a.Source.AuthoredNode == b.Source.AuthoredNode && a.Source.PathKey == b.Source.PathKey
        && a.Target.Node == b.Target.Node && a.Target.AuthoredNode == b.Target.AuthoredNode && a.Target.PathKey == b.Target.PathKey;
}

/** Local is the resource's own row; Composed carries the screen's node IDs. */
public sealed record CuiComposedBinding(CuiResourceInstance Instance, CuiBinding Local, CuiBinding Composed);

/** Maps bindings of composed CUI resources onto the composed hierarchy. */
public static class CuiComposedBindings
{
    /**
     * Maps every resource instance's bindings into the hierarchy's node IDs.
     * @param scene Composed hierarchy.
     * @return Bindings whose two nodes both exist in the scene, in instance order.
     */
    public static IReadOnlyList<CuiComposedBinding> Read(CuiComposition scene)
    {
        Dictionary<uint, IReadOnlyList<CuiBinding>> rows = [];
        List<CuiComposedBinding> result = [];
        foreach (var instance in scene.Instances)
        {
            if (!rows.TryGetValue(instance.Resource, out var local))
                rows[instance.Resource] = local = CuiBindings.Read(scene.Resources[instance.Resource].Data);
            foreach (var binding in local)
            {
                if (scene.ComposedId(instance, binding.Source.Node) is not { } source
                    || scene.ComposedId(instance, binding.Target.Node) is not { } target) continue;
                result.Add(new(instance, binding, binding with
                {
                    Source = binding.Source with { Node = checked((ushort)source) },
                    Target = binding.Target with { Node = checked((ushort)target) }
                }));
            }
        }
        return result;
    }
}

/** Type tag for a preview value. */
public enum CuiValueKind { Bool, Integer, Float, Hash }
/** Bool bits are 0 or 1; integer, float and hash bits are the native 32-bit value. */
public readonly record struct CuiPreviewValue(CuiValueKind Kind, uint Bits)
{
    /** Wraps a bool as a preview value. */
    public static CuiPreviewValue FromBool(bool value) => new(CuiValueKind.Bool, value ? 1u : 0u);
    public bool AsBool => Bits != 0;
}

/** Evaluates bindings from explicit inputs; values the running game would supply stay unset. */
public static class CuiBindingPreview
{
    /** The base layout property 0x201 is visibility; a sub-object path is a different property. */
    public const uint VisibilitySelector = 0x201;

    /**
     * Propagates explicit inputs through the bindings until no value changes.
     * @param bindings Bindings of one root resource.
     * @param inputs Explicit values by node and path; an input is never overwritten.
     * @return Inputs plus every value a binding produced.
     */
    public static IReadOnlyDictionary<(ushort Node, string Path), CuiPreviewValue> Evaluate(IReadOnlyList<CuiBinding> bindings,
        IReadOnlyDictionary<(ushort Node, string Path), CuiPreviewValue> inputs)
    {
        var values = new Dictionary<(ushort Node, string Path), CuiPreviewValue>(inputs);
        // A chain cannot be longer than the binding list, so this bounds any cycle.
        for (int pass = 0; pass <= bindings.Count; pass++)
        {
            bool changed = false;
            foreach (var binding in bindings)
            {
                var target = (binding.Target.Node, binding.Target.PathKey);
                if (binding.Error.Length != 0 || inputs.ContainsKey(target)
                    || !values.TryGetValue((binding.Source.Node, binding.Source.PathKey), out var source)
                    || Apply(binding.Transform, source) is not { } result
                    || values.TryGetValue(target, out var previous) && previous == result) continue;
                values[target] = result; changed = true;
            }
            if (!changed) break;
        }
        return values;
    }

    /** The value kind a binding reads, or null when only the source type could say. */
    public static CuiValueKind? SourceKind(CuiBinding binding) => binding.Transform.Kind switch
    {
        CuiBindingTransformKind.Not => CuiValueKind.Bool,
        CuiBindingTransformKind.CompareInteger => CuiValueKind.Integer,
        CuiBindingTransformKind.CompareFloat => CuiValueKind.Float,
        CuiBindingTransformKind.CompareHash => CuiValueKind.Hash,
        _ => IsVisibility(binding.Target) ? CuiValueKind.Bool : null
    };

    /** True when the endpoint addresses a node's visibility. */
    public static bool IsVisibility(CuiBindingEndpoint endpoint) => endpoint.Path is [VisibilitySelector];

    private static CuiPreviewValue? Apply(CuiBindingTransform transform, CuiPreviewValue value) => transform.Kind switch
    {
        CuiBindingTransformKind.None => value,
        CuiBindingTransformKind.Not => value.Kind == CuiValueKind.Bool ? CuiPreviewValue.FromBool(!value.AsBool) : null,
        CuiBindingTransformKind.CompareInteger when value.Kind == CuiValueKind.Integer =>
            CuiPreviewValue.FromBool(Compare(unchecked((int)value.Bits).CompareTo(unchecked((int)transform.Operand)), transform.Comparison)),
        CuiBindingTransformKind.CompareFloat when value.Kind == CuiValueKind.Float =>
            CuiPreviewValue.FromBool(Compare(BitConverter.UInt32BitsToSingle(value.Bits).CompareTo(BitConverter.UInt32BitsToSingle(transform.Operand)), transform.Comparison)),
        CuiBindingTransformKind.CompareHash when value.Kind == CuiValueKind.Hash =>
            CuiPreviewValue.FromBool(Compare(value.Bits.CompareTo(transform.Operand), transform.Comparison)),
        _ => null
    };

    private static bool Compare(int order, CuiComparison comparison) => comparison switch
    {
        CuiComparison.Equal => order == 0, CuiComparison.NotEqual => order != 0,
        CuiComparison.Less => order < 0, CuiComparison.LessOrEqual => order <= 0,
        CuiComparison.GreaterOrEqual => order >= 0, _ => order > 0
    };
}
