using System.Numerics;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/**
 * A channel's value in the game at rest. Unsettled names the run-time input it waits on; Value is then 1.
 * Changeable is a component config that names a start value this one reads; it may change it at run time.
 */
public readonly record struct ObjectChannelRestValue(Vector4 Value, string? Unsettled, uint Changeable = 0)
{
    public bool Settled => Unsettled is null;
    /** A value that is not settled because of the run-time input named by the reason. */
    public static ObjectChannelRestValue Open(string reason) => new(new Vector4(SceneObjectChannel.ViewerDefault), reason);
}

/**
 * At-rest values of an entity's value provider, the source of its object channels.
 * Provider rows start at their stored value; expression rows rerun when an input changes
 * (0x7FF742245F90), bound-input rows copy their source, and the rest keep their value until written.
 */
internal sealed class ObjectChannelRest
{
    private const uint ProviderPrimary = 0x8080979F, ProviderResource = 0x80809790, ProviderRow = 0x808097A1,
        ProviderSource = 0x808097A0, StoredValue = 0x80800090;
    private const int RowStride = 112, SourceStride = 12;
    private const int Rows = 0xD8, Sources = 0x108, TimeOutput = 0xC0, Stored = 0x50;
    // Row fields: name, expression code and constants, input outputs, bound input slot.
    private const int RowName = 0, RowCode = 0x08, RowConstants = 0x18, RowInputs = 0x38, RowSlot = 0x48;
    private const ushort NoSlot = 0xFFFF;
    // FNV-1 of the empty name: a source in the provider's own object.
    private const uint OwnObject = 0x811C9DC5;
    // FNV-1 of "time": a parameter the game rewrites each frame.
    private const uint TimeName = 0x9E3669DA;

    // Constant publisher rows (getter 0x7FF7427F1470): name +0x28, authored float4 +0x30.
    private const uint ConstantResource = 0x80808BE6, ConstantScalar = 0x80808BEC, ConstantVector = 0x80808BEA;
    private static readonly (int Field, uint Class)[] ConstantLists = [(0x90, ConstantScalar), (0xA0, ConstantScalar), (0xB0, ConstantVector)];
    private const int ConstantStride = 64, ConstantName = 0x28, ConstantValue = 0x30;
    // Damage value rows (getter 0x7FF742996CE0): name +0x28, kind +0x34, flags +0x35.
    private const uint DamageResource = 0x80804B8A, DamageValue = 0x80804B92;
    private const int DamageList = 0x398, DamageStride = 56, DamageName = 0x28, DamageKind = 0x34, DamageFlags = 0x35;
    private const byte AliveKind = 3, DividedByMaximum = 1, MaximumMinusValue = 2;
    // Model components hold the channel requests; their names are readers, not writers.
    private const uint ModelResource = 0x808072BD, ClothModelResource = 0x80807286, VariantResource = 0x808038A1;

    private sealed record Row(uint Name, byte[] Code, Vector4[] Constants, int[] Inputs, ushort Slot);

    private readonly Row[] rows = [];
    private readonly Vector4[] stored = [];
    private readonly Dictionary<int, (uint Scope, uint Name)> sources = [];
    private readonly Dictionary<uint, int> byName = [];
    private readonly Dictionary<uint, ObjectChannelRestValue> published = [];
    private readonly Dictionary<int, ObjectChannelRestValue> memo = [];
    private readonly HashSet<int> visiting = [];
    // Aligned words of the entity's other component configs, each with the first config holding it.
    private readonly Dictionary<uint, uint> namedBy = [];
    private readonly int timeOutput = -1;

    /** Reads the provider and the value publishers among the entity's component configs; the first provider is used. */
    public ObjectChannelRest(IEnumerable<(uint Tag, TagData Data)> configs)
    {
        foreach (var (tag, config) in configs)
        {
            int primary = config.Relative(0x10), resource = config.Relative(0x18);
            if (primary < 4 || resource < 4 || !config.Has(resource, 4)) continue;
            uint primaryClass = config.U32(primary - 4), resourceClass = config.U32(resource - 4);
            if (resourceClass == ConstantResource) ReadConstants(config, resource);
            else if (resourceClass == DamageResource) ReadDamage(config, resource);
            else if (primaryClass == ProviderPrimary && resourceClass == ProviderResource && rows.Length == 0)
            {
                rows = config.Rows(resource + Rows, ProviderRow, RowStride).Select(row => ReadRow(config, row)).ToArray();
                stored = config.Rows(primary + Stored, StoredValue, 16).Select(at => V4(config, at)).ToArray();
                foreach (int source in config.Rows(resource + Sources, ProviderSource, SourceStride))
                    sources[config.I32(source + 8)] = (config.U32(source), config.U32(source + 4));
                timeOutput = config.I32(resource + TimeOutput);
            }
            else if (resourceClass == SequenceResource.ResourceClass && SequenceRest.Read(tag, config, resource) is { } sequence)
            {
                // A sequence's input channels are readers; only its outputs and named writes can change a row.
                sequences.Add(sequence);
                foreach (uint name in sequence.Writes) namedBy.TryAdd(name, tag);
            }
            // Variant values are part filter labels; no variant method calls a provider writer.
            else if (resourceClass is not (ModelResource or ClothModelResource or VariantResource))
                for (int at = 0; at + 4 <= config.Length; at += 4) namedBy.TryAdd(config.U32(at), tag);
        }
        for (int i = 0; i < rows.Length; i++) byName.TryAdd(rows[i].Name, i);
    }

    private readonly List<SequenceRest> sequences = [];
    private readonly HashSet<uint> sequenceVisiting = [];

    /**
     * The value sequence outputs of this name hold at rest; null when no writer of it runs at rest.
     * That an output sets the provider value of its name is assumed: the binding is not read.
     */
    private ObjectChannelRestValue? SequenceValue(uint name)
    {
        if (!sequenceVisiting.Add(name)) return ObjectChannelRestValue.Open("sequence gate reads its own output");
        try
        {
            ObjectChannelRestValue? found = null;
            foreach (var sequence in sequences)
            {
                if (sequence.Rest(name, Channel) is not { } value) continue;
                if (found is { } other && (other.Unsettled is not null || other.Value != value.Value))
                    return ObjectChannelRestValue.Open("two sequences write this value");
                found = value;
            }
            return found;
        }
        finally { sequenceVisiting.Remove(name); }
    }

    public bool HasProvider => rows.Length != 0;

    // Model resource +0x2A8: one 40-byte request per TFX object channel, named at request +0x20.
    private const int ChannelRequests = 0x2A8, ChannelRequestStride = 40, ChannelName = 0x20;
    private const uint ChannelRequestClass = 0x80809789;

    /** Channel names of a model resource in TFX channel order; empty when it has none. */
    public static uint[] RequestNames(TagData config, int resource) =>
        !config.Has(resource + ChannelRequests, 16) || config.I64(resource + ChannelRequests) == 0 ? []
            : config.Rows(resource + ChannelRequests, ChannelRequestClass, ChannelRequestStride)
                .Select(row => config.U32(row + ChannelName)).ToArray();

    /** Each named channel's rest value, read from the entity's component configs. */
    public static ObjectChannelRestValue[] RestValues(IEnumerable<(uint Tag, TagData Data)> configs, uint[] names)
    {
        var provider = new ObjectChannelRest(configs);
        return provider.HasProvider ? names.Select(provider.Channel).ToArray()
            : names.Select(_ => ObjectChannelRestValue.Open("entity has no value provider")).ToArray();
    }

    /** The value a channel request of this name reads at rest. */
    public ObjectChannelRestValue Channel(uint name) =>
        byName.TryGetValue(name, out int row) ? Output(row) : ObjectChannelRestValue.Open("no provider output of this name");

    private ObjectChannelRestValue Output(int index)
    {
        if (memo.TryGetValue(index, out var known)) return known;
        // A row that reads itself settles on a fixed point the viewer does not solve.
        if (!visiting.Add(index)) return ObjectChannelRestValue.Open("provider row reads its own value");
        var row = rows[index];
        ObjectChannelRestValue value;
        if (index == timeOutput) value = ObjectChannelRestValue.Open("simulation time");
        else if (row.Code.Length != 0)
        {
            var inputs = new Vector4[row.Inputs.Length];
            value = default;
            uint changeable = 0;
            for (int i = 0; i < inputs.Length && value.Unsettled is null; i++)
            {
                var input = row.Inputs[i] >= 0 && row.Inputs[i] < rows.Length ? Output(row.Inputs[i])
                    : ObjectChannelRestValue.Open("expression input out of range");
                if (input.Unsettled is not null) value = input;
                else if (changeable == 0) changeable = input.Changeable;
                inputs[i] = input.Value;
            }
            if (value.Unsettled is null)
                value = ProviderExpression.Run(row.Code, row.Constants, inputs) is { } result ? new(result, null, changeable)
                    : ObjectChannelRestValue.Open("expression uses an operation the viewer does not run");
        }
        else if (row.Slot != NoSlot)
        {
            // A bound input mirrors its source; only a source published in this entity is known.
            value = !sources.TryGetValue(index, out var source) ? ObjectChannelRestValue.Open("bound input without a source")
                : source.Scope != OwnObject ? ObjectChannelRestValue.Open($"input from scope 0x{source.Scope:X8}")
                : published.TryGetValue(source.Name, out var publishedValue) ? publishedValue
                : SequenceValue(source.Name) ?? ObjectChannelRestValue.Open($"run-time input 0x{source.Name:X8}");
        }
        else if (published.TryGetValue(row.Name, out var publishedValue)) value = publishedValue;
        else if (row.Name == TimeName) value = ObjectChannelRestValue.Open("simulation time");
        else if (SequenceValue(row.Name) is { } sequenceValue) value = sequenceValue;
        // A row no component of the entity can write is set from outside the entity data.
        else if (!namedBy.TryGetValue(row.Name, out uint writer)) value = ObjectChannelRestValue.Open($"run-time input 0x{row.Name:X8}");
        else value = new(index < stored.Length ? stored[index] : Vector4.Zero, null, writer);
        visiting.Remove(index);
        return memo[index] = value;
    }

    private static Row ReadRow(TagData data, int row) => new(data.U32(row + RowName),
        data.RowsOfAnyClass(row + RowCode, 1).Select(data.U8).ToArray(),
        data.RowsOfAnyClass(row + RowConstants, 16).Select(at => V4(data, at)).ToArray(),
        data.RowsOfAnyClass(row + RowInputs, 4).Select(data.I32).ToArray(), data.U16(row + RowSlot));

    private void ReadConstants(TagData data, int resource)
    {
        foreach (var (field, rowClass) in ConstantLists)
        {
            if (!data.Has(field + resource, 16) || data.ArrayClass(resource + field) != rowClass) continue;
            foreach (int row in data.Rows(resource + field, rowClass, ConstantStride))
                published.TryAdd(data.U32(row + ConstantName), new(V4(data, row + ConstantValue), null));
        }
    }

    /** A spawned actor is alive at full health: a fraction or the alive kind reads 1, and maximum minus value reads 0. */
    private void ReadDamage(TagData data, int resource)
    {
        if (!data.Has(resource + DamageList, 16) || data.ArrayClass(resource + DamageList) != DamageValue) return;
        foreach (int row in data.Rows(resource + DamageList, DamageValue, DamageStride))
        {
            byte kind = data.U8(row + DamageKind), flags = data.U8(row + DamageFlags);
            bool inverted = (flags & MaximumMinusValue) != 0;
            var value = inverted || (flags & DividedByMaximum) != 0 || kind == AliveKind
                ? new ObjectChannelRestValue(new Vector4(inverted ? 0 : 1), null)
                : ObjectChannelRestValue.Open("damage amount without its maximum");
            published.TryAdd(data.U32(row + DamageName), value);
        }
    }

    private static Vector4 V4(TagData data, int at) => new(data.F32(at), data.F32(at + 4), data.F32(at + 8), data.F32(at + 12));
}

/**
 * Values a sequence player's outputs hold at rest. A type-2 work node writes its output with an expression of
 * the node's progress (0x7FF7420CDA40); progress is 0 for a node with no duration and 1 when a timed node ends.
 */
internal sealed class SequenceRest
{
    // Output rows: 48 bytes each, named at +0x28.
    private const int Outputs = 0x178, OutputStride = 48, OutputName = 0x28;
    private const byte WriteOutput = 2, WriteByName = 3, SerialFlow = 0, RandomFlow = 2, GateFlow = 6, ChannelCondition = 0;
    // A type-3 work node names the provider row it writes at body +0x68.
    private const int WrittenName = 0x68;
    private const int FlowKind = SequenceResource.FlowKind, WorkKind = SequenceResource.WorkKind;
    // Expression opcode that pushes an input; the node's progress is input 0.
    private const byte PushInput = 0x3C;

    /** Type-2 work node fields: reset to zero on stop +0x28, output +0x2C, expression code +0x30 and constants +0x40. */
    private sealed record WriterNode(bool Reset, int Output, byte[] Code, Vector4[] Constants);

    private readonly uint config;
    private readonly bool autoStart;
    private readonly SequenceNode[] flows, works;
    // Null for a work node that is not type 2.
    private readonly WriterNode?[] writerNodes;
    private readonly uint[] inputs, outputs;
    private readonly Dictionary<(int Kind, int Index), (int Flow, int Position)[]> paths;

    private SequenceRest(uint config, TagData data, int resource)
    {
        this.config = config;
        var sequence = new SequenceResource(data, resource);
        (autoStart, flows, works, inputs, paths) = (sequence.AutoStart, sequence.Flows, sequence.Works, sequence.Inputs, sequence.Paths);
        writerNodes = works.Select(work => work.Type == WriteOutput ? ReadWriter(data, work.Body) : null).ToArray();
        outputs = data.I64(resource + Outputs) == 0 ? []
            : data.RowsOfAnyClass(resource + Outputs, OutputStride).Select(row => data.U32(row + OutputName)).ToArray();
        var written = new List<uint>(outputs);
        foreach (var work in works)
            if (work.Type == WriteByName && data.Has(work.Body + WrittenName, 4)) written.Add(data.U32(work.Body + WrittenName));
        Writes = written;
    }

    /** Names this sequence can write: its outputs and the provider rows its type-3 nodes name. */
    public IReadOnlyList<uint> Writes { get; }

    /** @return Null when the config holds no readable sequence. */
    public static SequenceRest? Read(uint config, TagData data, int resource)
    {
        try { return data.Has(resource, SequenceResource.Flags + 1) ? new SequenceRest(config, data, resource) : null; }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException
            or IndexOutOfRangeException) { return null; }
    }

    /**
     * The value this sequence's output of that name holds at rest; null when it has none or no writer runs.
     * @param channel Rest value of an input channel, for gates and start conditions.
     */
    public ObjectChannelRestValue? Rest(uint name, Func<uint, ObjectChannelRestValue> channel)
    {
        int output = Array.IndexOf(outputs, name);
        if (output < 0 || !autoStart) return null;
        var writers = new List<(float End, Vector4 Value)>();
        for (int index = 0; index < works.Length; index++)
        {
            var work = works[index];
            if (work.Type != WriteOutput || writerNodes[index]!.Output != output || !paths.TryGetValue((WorkKind, index), out var path)) continue;
            var writer = writerNodes[index]!;
            float start = Finite(work.DelayMin);
            bool runs = true, loops = false;
            foreach (var (flowIndex, position) in path)
            {
                var flow = flows[flowIndex];
                start += Finite(flow.DelayMin);
                if (flow.Type == RandomFlow) return Open("a random sequence flow picks the writer");
                loops |= flow.Loop != 0 && flow.Type != GateFlow;
                var gate = flow.Type == GateFlow ? new SequenceCondition(ChannelCondition, flow.RangeMin, flow.RangeMax, flow.RangeChannel) : null;
                foreach (var condition in new[] { gate, flow.Start })
                {
                    if (condition is null) continue;
                    if (condition.Kind != ChannelCondition) return Open("a sequence start condition is not a channel range");
                    if (condition.Channel < 0 || condition.Channel >= inputs.Length) return Open("a sequence gate names no channel");
                    var value = channel(inputs[condition.Channel]);
                    if (value.Unsettled is not null) return Open($"a sequence gate reads 0x{inputs[condition.Channel]:X8}, which is not settled");
                    runs &= value.Value.X >= condition.Min && condition.Max > value.Value.X;
                }
                if (flow.Type != SerialFlow) continue;
                // A serial child starts when the one before it ends; one without a timer never ends.
                for (int earlier = 0; earlier < position && runs; earlier++)
                {
                    var (kind, at) = flow.Children[earlier];
                    var sibling = kind == FlowKind && at < flows.Length ? flows[at] : kind == WorkKind && at < works.Length ? works[at] : null;
                    if (sibling is not { Timed: true }) runs = false;
                    else start += Finite(sibling.DelayMin) + Finite(sibling.DurationMax);
                }
            }
            if (!runs) continue;
            bool progress = ReadsInput(writer.Code);
            if (loops && progress) return Open("a looping sequence flow repeats the writer");
            if (!work.Timed && progress) return Open("an untimed writer reads its progress");
            float duration = Finite(work.DurationMax);
            float ended = work.Timed && duration > 0 ? 1 : 0;
            var result = work.Timed && writer.Reset ? Vector4.Zero
                : ProviderExpression.Run(writer.Code, writer.Constants, [new Vector4(ended)]);
            if (result is not { } written) return Open("a sequence writer uses an operation the viewer does not run");
            writers.Add((work.Timed ? start + duration : float.PositiveInfinity, written));
        }
        if (writers.Count == 0) return null;
        float last = writers.Max(writer => writer.End);
        var values = writers.Where(writer => writer.End == last).Select(writer => writer.Value).Distinct().ToArray();
        return values.Length == 1 ? new ObjectChannelRestValue(values[0], null, config)
            : Open("sequence writers end together with different values");
    }

    private ObjectChannelRestValue Open(string reason) => ObjectChannelRestValue.Open(reason) with { Changeable = config };

    /** Opcodes 0x22 and 0x34 to 0x3E take one operand byte, as ProviderExpression reads them. */
    private static bool ReadsInput(byte[] code)
    {
        for (int at = 0; at < code.Length; at += code[at] is 0x22 or >= 0x34 and <= 0x3E ? 2 : 1)
            if (code[at] == PushInput) return true;
        return false;
    }

    private static WriterNode ReadWriter(TagData data, int body)
    {
        if (!data.Has(body, 0x50)) throw new InvalidDataException($"Sequence writer at 0x{body:X} leaves its config.");
        return new(data.U8(body + 0x28) != 0, data.I32(body + 0x2C),
            data.I64(body + 0x30) != 0 ? data.RowsOfAnyClass(body + 0x30, 1).Select(data.U8).ToArray() : [],
            data.I64(body + 0x40) != 0 ? data.RowsOfAnyClass(body + 0x40, 16).Select(at => new Vector4(
                data.F32(at), data.F32(at + 4), data.F32(at + 8), data.F32(at + 12))).ToArray() : []);
    }

    /** Delays and durations that are not finite read as zero. */
    private static float Finite(float value) => float.IsFinite(value) ? value : 0;
}

/**
 * Rest channel tables of entity models. No authored spawn input reaches the value provider, so a table
 * depends on the entity and its model config only. Equal tables are one shared array.
 */
public sealed class ObjectChannelTables(PackageCatalog catalog)
{
    private const uint ConfigClass = 0x80809C36, ComponentRow = 0x80809C04, ModelResource = 0x808072BD;
    private readonly Dictionary<(uint Entity, uint Config), (uint[] Names, Vector4[]? Table)> models = [];
    private readonly Dictionary<Vector4[], Vector4[]> shared = new(TableComparer.Instance);
    private readonly Dictionary<uint, TagData> tags = [];

    /** Distinct tables handed out; a model whose channels all rest at one is not counted. */
    public int DistinctTables => shared.Count;
    /** Models whose channels did not read; they take the viewer default. */
    public int Unread { get; private set; }

    /** Channel names of the model config in TFX order; empty when it has none or does not read. */
    public IReadOnlyList<uint> Names(uint entity, uint modelConfig) => Model(entity, modelConfig).Names;

    /** The model's channel values at rest; null when every one rests at one, the viewer default. */
    public IReadOnlyList<Vector4>? Table(uint entity, uint modelConfig) => Model(entity, modelConfig).Table;

    private (uint[] Names, Vector4[]? Table) Model(uint entity, uint config)
    {
        if (models.TryGetValue((entity, config), out var known)) return known;
        (uint[] Names, Vector4[]? Table) result = ([], null);
        try
        {
            var data = Get(config);
            int resource = data.Relative(0x18);
            if (resource >= 4 && data.Has(resource, 4) && data.U32(resource - 4) == ModelResource
                && ObjectChannelRest.RequestNames(data, resource) is { Length: > 0 } names)
            {
                var definition = Get(entity);
                var configs = definition.Rows(0x10, ComponentRow, 12).Select(row => definition.U32(row))
                    .Where(tag => catalog.TryGetEntry(tag, out var entry) && entry.Reference == ConfigClass)
                    .Select(tag => (tag, Get(tag)));
                var values = ObjectChannelRest.RestValues(configs, names).Select(rest => rest.Value).ToArray();
                Vector4[]? table = null;
                if (values.Any(value => value != Vector4.One) && !shared.TryGetValue(values, out table))
                    shared.Add(values, table = values);
                result = (names, table);
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException
            or IOException or KeyNotFoundException or IndexOutOfRangeException)
        { Unread++; }
        return models[(entity, config)] = result;
    }

    private TagData Get(uint tag)
    {
        if (!tags.TryGetValue(tag, out var data)) tags.Add(tag, data = new(catalog.Read(tag)));
        return data;
    }

    /** Tables compare by their float bits, so equal values share one array. */
    private sealed class TableComparer : IEqualityComparer<Vector4[]>
    {
        public static readonly TableComparer Instance = new();
        /** Compares tables by their bytes. */
        public bool Equals(Vector4[]? a, Vector4[]? b) => ReferenceEquals(a, b) || a is not null && b is not null
            && System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan())
                .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.AsSpan()));
        /** Hashes the table's bytes. */
        public int GetHashCode(Vector4[] table)
        {
            var hash = new HashCode();
            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(table.AsSpan()));
            return hash.ToHashCode();
        }
    }
}
