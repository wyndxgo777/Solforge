using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace SunriseStudio.Scene.Collision;

/**
 * A Havok 2012 binary packfile: the three sections, pointer fixups, object classes and imports.
 * Offsets taken or returned by members are relative to the data section unless named absolute.
 */
public sealed class HavokPackfile
{
    // Magic, file version 9 and 8-byte little-endian pointers: the only layout the game ships.
    private const uint Magic0 = 0x57E0E057, Magic1 = 0x10C0C010;
    private const int FileVersion = 9, SectionHeaderStart = 0x40, SectionHeaderSize = 0x30;
    private static readonly byte[] LayoutRules = [8, 1, 0, 1];
    private const string ContentsVersion = "hk_2012.2.0-r1";

    private readonly Dictionary<int, int> local = [], global = [];
    private readonly List<(int Source, int Target)> globalOrder = [];
    private readonly Dictionary<int, string> objects = [];
    private readonly Dictionary<int, string> imports = [];
    private readonly Dictionary<string, int> classOffsets = new(StringComparer.Ordinal);

    public byte[] Bytes { get; }
    /** Role: 0 map compound or cloth, 1 per-mesh shape, 2 shape array, 3 object physics. */
    public int UserTag { get; }
    public int DataStart { get; }
    public int DataLength { get; }
    public int RootOffset { get; }
    public string RootClass { get; }
    /** Class name of every object, keyed by its data offset. */
    public IReadOnlyDictionary<int, string> Objects => objects;
    /** Cross-packfile pointer names, keyed by the data offset of the pointer field. */
    public IReadOnlyDictionary<int, string> Imports => imports;
    /** Local fixups, pointer field to data offset. */
    public IReadOnlyDictionary<int, int> LocalFixups => local;
    /** Data-section global fixups in file order; a writer keeps the order to keep the bytes. */
    public IReadOnlyList<(int Source, int Target)> GlobalFixups => globalOrder;
    /** Offset of each class name in the class name section, as virtual fixups name it. */
    public IReadOnlyDictionary<string, int> ClassNameOffsets => classOffsets;

    private HavokPackfile(byte[] bytes)
    {
        Bytes = bytes;
        if (bytes.Length < SectionHeaderStart || U32Absolute(0) != Magic0 || U32Absolute(4) != Magic1)
            throw new InvalidDataException("Not a Havok packfile.");
        UserTag = I32Absolute(8);
        if (I32Absolute(12) != FileVersion || !bytes.AsSpan(16, 4).SequenceEqual(LayoutRules))
            throw new InvalidDataException("Havok packfile layout is not the 2012 64-bit layout.");
        if (CString(0x28, 16) != ContentsVersion) throw new InvalidDataException("Havok contents version is not 2012.2.");
        int sectionCount = I32Absolute(0x14), contentsSection = I32Absolute(0x18);
        RootOffset = I32Absolute(0x1C);
        if (sectionCount is < 1 or > 16 || contentsSection < 0 || contentsSection >= sectionCount)
            throw new InvalidDataException("Havok section table is invalid.");
        var sections = Enumerable.Range(0, sectionCount).Select(ReadSection).ToArray();
        var names = ReadClassNames(sections.Single(s => s.Tag == "__classnames__"));
        foreach (var (offset, name) in names) classOffsets.TryAdd(name, offset);
        var data = sections[contentsSection];
        DataStart = data.Start;
        DataLength = data.Local;
        for (int at = data.Start + data.Local; at + 8 <= data.Start + data.Global; at += 8)
            if (I32Absolute(at) is var source and not -1) local[source] = I32Absolute(at + 4);
        // Global fixups {src, section, dst} point at objects; data-section targets are kept.
        for (int at = data.Start + data.Global; at + 12 <= data.Start + data.Virtual; at += 12)
            if (I32Absolute(at) is var source and not -1 && I32Absolute(at + 4) == contentsSection)
            {
                global[source] = I32Absolute(at + 8);
                globalOrder.Add((source, global[source]));
            }
        for (int at = data.Start + data.Virtual; at + 12 <= data.Start + data.Exports; at += 12)
        {
            int source = I32Absolute(at);
            if (source == -1) continue;
            objects[source] = names.TryGetValue(I32Absolute(at + 8), out var name)
                ? name : throw new InvalidDataException("Havok object names an unknown class.");
        }
        for (int at = data.Start + data.Imports; at + 4 < data.Start + data.End;)
        {
            int field = I32Absolute(at);
            if (field == -1) break;
            int end = System.Array.IndexOf(bytes, (byte)0, at + 4);
            if (end < 0) throw new InvalidDataException("Havok import name is unterminated.");
            imports[field] = Encoding.ASCII.GetString(bytes, at + 4, end - at - 4);
            at = (end + 1 + 3) & ~3;
        }
        RootClass = objects.TryGetValue(RootOffset, out var root) ? root : throw new InvalidDataException("Havok root object has no class.");
    }

    /** Parses a Havok packfile from its bytes. */
    public static HavokPackfile Parse(byte[] bytes) => new(bytes);

    /** True when the bytes start with the Havok packfile magic. */
    public static bool IsPackfile(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic0
        && BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == Magic1;

    /** Target of a pointer field, or null when it is null or points into another packfile. */
    public int? Pointer(int field) =>
        local.TryGetValue(field, out int target) || global.TryGetValue(field, out target) ? target : null;

    /** An hkArray field: its element count, and its data offset or -1 when empty. */
    public (int Count, int Data) Array(int field)
    {
        int count = I32(field + 8);
        if (count == 0) return (0, -1);
        if (count < 0 || !local.TryGetValue(field, out int data)) throw new InvalidDataException("Havok array has no data.");
        return (count, data);
    }

    /** Class of the object at a data offset, or null. */
    public string? ClassAt(int offset) => objects.GetValueOrDefault(offset);

    /** Converts a data-section offset to a file offset; throws if it leaves the data section. */
    public int Absolute(int offset)
    {
        if (offset < 0 || offset >= DataLength) throw new InvalidDataException("Havok offset leaves the data section.");
        return DataStart + offset;
    }

    /** Reads a byte at a data-section offset. */
    public byte U8(int offset) => Bytes[Absolute(offset)];
    /** Reads a little-endian uint16 at a data-section offset. */
    public ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(Absolute(offset), 2));
    /** Reads a little-endian int32 at a data-section offset. */
    public int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(Absolute(offset), 4));
    /** Reads a little-endian uint32 at a data-section offset. */
    public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(Absolute(offset), 4));
    /** Reads a little-endian uint64 at a data-section offset. */
    public ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(Bytes.AsSpan(Absolute(offset), 8));
    /** Reads a little-endian float at a data-section offset. */
    public float F32(int offset) => BinaryPrimitives.ReadSingleLittleEndian(Bytes.AsSpan(Absolute(offset), 4));
    /** Reads three floats at a data-section offset. */
    public Vector3 V3(int offset) => new(F32(offset), F32(offset + 4), F32(offset + 8));
    /** Reads four floats as a quaternion at a data-section offset. */
    public Quaternion Q4(int offset) => new(F32(offset), F32(offset + 4), F32(offset + 8), F32(offset + 12));

    private record struct Section(string Tag, int Start, int Local, int Global, int Virtual, int Exports, int Imports, int End);

    private Section ReadSection(int index)
    {
        int at = SectionHeaderStart + index * SectionHeaderSize;
        if (at + SectionHeaderSize > Bytes.Length) throw new InvalidDataException("Havok section header is truncated.");
        var section = new Section(CString(at, 19), I32Absolute(at + 0x14), I32Absolute(at + 0x18), I32Absolute(at + 0x1C),
            I32Absolute(at + 0x20), I32Absolute(at + 0x24), I32Absolute(at + 0x28), I32Absolute(at + 0x2C));
        int[] offsets = [section.Local, section.Global, section.Virtual, section.Exports, section.Imports, section.End];
        if (section.Start < 0 || offsets.Any(x => x < 0) || !offsets.SequenceEqual(offsets.Order())
            || (long)section.Start + section.End > Bytes.Length)
            throw new InvalidDataException("Havok section offsets are invalid.");
        return section;
    }

    /** Entries are {u32 signature, u8 0x09, name}. Objects name a class by its name offset. */
    private Dictionary<int, string> ReadClassNames(Section section)
    {
        var names = new Dictionary<int, string>();
        for (int at = section.Start; at < section.Start + section.Local - 5;)
        {
            if (U32Absolute(at) == uint.MaxValue) break;
            int end = System.Array.IndexOf(Bytes, (byte)0, at + 5);
            if (end < 0) throw new InvalidDataException("Havok class name is unterminated.");
            names[at + 5 - section.Start] = Encoding.ASCII.GetString(Bytes, at + 5, end - at - 5);
            at = end + 1;
        }
        return names;
    }

    private string CString(int at, int max)
    {
        int end = at;
        while (end < at + max && end < Bytes.Length && Bytes[end] != 0) end++;
        return Encoding.ASCII.GetString(Bytes, at, end - at);
    }

    private int I32Absolute(int at) => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(at, 4));
    private uint U32Absolute(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(at, 4));
}
