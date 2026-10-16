using System.Buffers.Binary;
using System.Collections.Immutable;

namespace SunriseStudio.Packages;

/** Compact source facts contain no borrowed package bytes or generated display names. */
public sealed class ComponentSlotConfigSeed
{
    internal ComponentSlotNode Node { get; }
    internal ComponentSlotConfigSeed(ComponentSlotNode node) => Node = node;
    public uint Tag => Node.Tag;
    public uint PrimaryClass => Node.Primary;
    public int DescriptorCount => Node.Descriptors.Length;
    public string? Diagnostic => Node.Error;
}

/** One slot row of a component node: slot type and the row's offset. */
internal readonly record struct ComponentSlotRow(ushort Type, int Offset);
/** One root of a component node: tag, offset, subblock offset and bubble. */
internal readonly record struct ComponentSlotRoot(uint Tag, int Offset, int SubblockOffset, int Bubble);
/** A link to another node in a descriptor chain. */
internal readonly record struct ComponentSlotLink(uint Tag, int Offset);
/** One slot descriptor: class, key, type, index, bubble, sense and auth ids, and its offset. */
internal readonly record struct ComponentSlotDescriptor(uint Class, uint Key, ushort Type, ushort Index,
    uint Bubble, uint Sense, uint Auth, int Offset);
/** Everything read from one owner, config, indirect or redirect node; Error is set when the node is unreadable. */
internal sealed record ComponentSlotNode(uint Tag, uint Class, int Length, uint Primary, uint Key,
    ImmutableArray<ComponentSlotRow> Slots, ImmutableArray<ComponentSlotRoot> Roots,
    ImmutableArray<ComponentSlotDescriptor> Descriptors, ImmutableArray<ComponentSlotLink> Next, string? Error)
{
    /** Placeholder node carrying only a read error. */
    internal static ComponentSlotNode Failed(uint tag, uint type, string error) =>
        new(tag, type, 0, 0, 0, [], [], [], [], error);
}

/** Bounds-checked parser for the component slot node classes. */
internal static class ComponentSlotReader
{
    internal const uint OwnerClass = 0x80809462, ConfigClass = 0x80809C36,
        IndirectClass = 0x80809468, RedirectClass = 0x80809B14;
    internal const int MaximumRows = 1_000_000;
    private const int DescriptorBytes = 128;

    /** True for the four node classes the reader understands. */
    internal static bool Supported(uint type) => type is OwnerClass or ConfigClass or IndirectClass or RedirectClass;

    /** Exact typed arrays and bounded pointers precede every owner or chain fact. */
    internal static ComponentSlotNode Parse(uint tag, uint type, ReadOnlySpan<byte> bytes)
    {
        try
        {
            var data = new Data(bytes);
            if (type == ConfigClass) return Config(tag, data);
            if (type == OwnerClass)
            {
                uint key = data.U32(12);
                var slots = data.Array(32, 0x80809B3A, 8);
                if (slots.Count > ushort.MaxValue + 1) throw new InvalidDataException("Owner slot ordinals exceed the descriptor width.");
                var declarations = ImmutableArray.CreateBuilder<ComponentSlotRow>(slots.Count);
                for (int index = 0; index < slots.Count; index++)
                {
                    int row = slots.Start + index * 8;
                    uint slot = data.U32(row);
                    if (slot is 0 or > ushort.MaxValue) throw new InvalidDataException("Owner slot type is outside its stored width.");
                    declarations.Add(new((ushort)slot, row));
                }
                var roots = ImmutableArray.CreateBuilder<ComponentSlotRoot>();
                var blocks = data.Array(56, 0x80809464, 24);
                for (int index = 0; index < blocks.Count; index++)
                {
                    int block = blocks.Start + index * 24;
                    var handles = data.Array(block + 8, 0x80809466, 4);
                    if (handles.Count > MaximumRows - roots.Count) throw new InvalidDataException("Owner handle count exceeds the extraction limit.");
                    for (int handle = 0; handle < handles.Count; handle++)
                    {
                        int at = handles.Start + handle * 4;
                        roots.Add(new(data.U32(at), at, block, data.I32(block)));
                    }
                }
                return new(tag, type, data.Length, 0, key, declarations.MoveToImmutable(), roots.ToImmutable(), [], [], null);
            }
            if (type == RedirectClass)
                return new(tag, type, data.Length, 0, 0, [], [], [], [new(data.U32(12), 12)], null);
            if (type == IndirectClass)
            {
                if (data.U32(8) != uint.MaxValue) return new(tag, type, data.Length, 0, 0, [], [], [], [], null);
                var targets = data.Array(16, 0x80809B13, 4);
                if (targets.Count == 0 || targets.Count > 256) throw new InvalidDataException("Descriptor redirect count is invalid.");
                var next = ImmutableArray.CreateBuilder<ComponentSlotLink>(targets.Count);
                for (int index = 0; index < targets.Count; index++) next.Add(new(data.U32(targets.Start + index * 4), targets.Start + index * 4));
                return new(tag, type, data.Length, 0, 0, [], [], [], next.MoveToImmutable(), null);
            }
            return ComponentSlotNode.Failed(tag, type, "Unsupported descriptor-chain class.");
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        {
            return ComponentSlotNode.Failed(tag, type, error.Message);
        }
    }

    /** Primary equality admits component roles only after complete descriptor fields are present. */
    private static ComponentSlotNode Config(uint tag, Data data)
    {
        uint primary = 0;
        if (data.Length >= 32 && data.I64(16) != 0) primary = data.U32(data.Relative(16, 4, 0) - 4);
        var descriptors = ImmutableArray.CreateBuilder<ComponentSlotDescriptor>();
        for (int at = 0; at <= data.Length - DescriptorBytes; at += 4)
        {
            if (data.U32(at) != tag || data.U32(at + 8) != 0x70) continue;
            uint component = data.U32(at + 4), sense = data.U32(at + 68), auth = data.U32(at + 72);
            if (component != primary || !ClassId(component) || !Schema(sense) || !Schema(auth)) continue;
            if (descriptors.Count == MaximumRows) throw new InvalidDataException("Config descriptor count exceeds the extraction limit.");
            descriptors.Add(new(component, data.U32(at + 48), data.U16(at + 52), data.U16(at + 54),
                data.U32(at + 56), sense, auth, at));
        }
        return new(tag, ConfigClass, data.Length, primary, 0, [], [], descriptors.ToImmutable(), [], null);
    }

    /** True when a value is a class id (0x8080xxxx). */
    internal static bool ClassId(uint value) => value >> 16 == 0x8080;
    /** True for a class id or the 0xFFFFFFFF none marker. */
    internal static bool Schema(uint value) => value == uint.MaxValue || ClassId(value);

    /** Signed pointer bounds are checked before addition, including high-bit malformed inputs. */
    private readonly ref struct Data
    {
        private readonly ReadOnlySpan<byte> bytes;
        internal Data(ReadOnlySpan<byte> bytes) => this.bytes = bytes;
        internal int Length => bytes.Length;
        private ReadOnlySpan<byte> At(int offset, int count) => offset < 0 || count < 0 || offset > bytes.Length - count
            ? throw new InvalidDataException("Component role data leaves its source.") : bytes.Slice(offset, count);
        /** Reads a little-endian uint, bounds-checked. */
        internal uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(At(at, 4));
        /** Reads a little-endian int, bounds-checked. */
        internal int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(At(at, 4));
        /** Reads a little-endian ushort, bounds-checked. */
        internal ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(At(at, 2));
        /** Reads a little-endian long, bounds-checked. */
        internal long I64(int at) => BinaryPrimitives.ReadInt64LittleEndian(At(at, 8));
        /** Follows a self-relative pointer, requiring room for the bytes before and after it. */
        internal int Relative(int field, int before, int after)
        {
            long relative = I64(field);
            if (relative < (long)before - field || relative > bytes.Length - (long)after - field)
                throw new InvalidDataException("Component role pointer leaves its source.");
            return (int)(field + relative);
        }
        /** Reads a typed array header as start offset and count, capped at MaximumRows. */
        internal (int Start, int Count) Array(int field, uint type, int stride)
        {
            long count = I64(field), relative = I64(checked(field + 8));
            if (count == 0 && relative == 0) return (0, 0);
            if (count < 0 || count > MaximumRows) throw new InvalidDataException("Component role array count is invalid.");
            int header = Relative(checked(field + 8), 4, 16);
            if (U32(header - 4) != 0x80809FBD || I64(header) != count || U32(header + 8) != type
                || U32(header + 12) != 0 || count > (bytes.Length - header - 16L) / stride)
                throw new InvalidDataException("Component role typed array is invalid.");
            return (header + 16, (int)count);
        }
    }
}
