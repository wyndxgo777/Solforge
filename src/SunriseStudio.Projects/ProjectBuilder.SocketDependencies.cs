/** File: Partial ProjectBuilder: plans the socket (plug) dependencies the Collections constructor reads. */
using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

/** Partial: socket dependency planning. */
public static partial class ProjectBuilder
{
    private const uint SocketDependencyManifest = 0x81A2926F;
    private const uint SocketItemIndex = 0x81327CCB, SocketDisplayIndex = 0x81613CF1;
    private const uint SocketPlugSets = 0x81319349, SocketItemClass = 0x80807BEA;

    /** The Collections constructor reads plug definitions before its later plug-load pass. */
    private static Dictionary<uint, uint> PlanSocketDependencies(PackageCatalog catalog,
        IReadOnlyDictionary<uint, byte[]> edited, IReadOnlyList<AddedEntry> added)
    {
        var authored = added.ToDictionary(asset => asset.Tag);
        var items = added.Where(asset => asset.Class == SocketItemClass).Select(asset => (asset.Tag, asset.Data))
            .Concat(edited.Where(pair => catalog.TryGetEntry(pair.Key, out var entry) && entry.Reference == SocketItemClass)
                .Select(pair => (Tag: pair.Key, Data: pair.Value))).ToArray();
        if (items.Length == 0 || !added.Any(asset => asset.Class == SocketItemClass)) return [];
        byte[] Read(uint tag) => edited.TryGetValue(tag, out var current) ? current : catalog.Read(tag);
        byte[] definitions = Read(SocketItemIndex), displays = Read(SocketDisplayIndex), sets = Read(SocketPlugSets);
        int[] definitionRows = SocketRows(definitions, 8, 0x80807BE8, 24);
        int[] displayRows = SocketRows(displays, 8, 0x80805CDF, 24);
        int[] sharedRows = SocketRows(sets, 8, 0x80802DFF, 24);
        if (definitionRows.Length != displayRows.Length)
            throw new InvalidDataException("Socket dependency item tables do not match.");
        var result = new Dictionary<uint, uint>();
        var pending = new Queue<(uint Tag, byte[] Data)>(items.Where(item => item.Data.Length < 188 || item.Data[184] <= 2));
        var visited = new HashSet<uint>();
        while (pending.TryDequeue(out var current))
        {
            var (owner, data) = current;
            if (!visited.Add(owner)) continue;
            if (data.Length < 188 || SocketI64(data, 0) != data.Length)
                throw new InvalidDataException("A socket dependency item is truncated.");
            long relative = SocketI64(data, 104);
            if (relative == 0) continue;
            int block = checked((int)(104 + relative));
            if (block < 4 || block > data.Length - 32 || SocketU32(data, block - 4) != 0x808077C1)
                throw new InvalidDataException("A socket dependency block has an unsupported type.");
            int[] sockets = SocketRows(data, block, 0x808077C4, 80);
            if (sockets.Length > 12) throw new InvalidDataException("A socket dependency item exceeds twelve lanes.");
            foreach (int socket in sockets)
            {
                Add(SocketU16(data, socket + 2), owner);
                Members(data, socket + 64, owner);
                foreach (int field in new[] { 12, 32 })
                {
                    ushort shared = SocketU16(data, socket + field);
                    if (shared == ushort.MaxValue) continue;
                    if (shared >= sharedRows.Length)
                        throw new InvalidDataException("A socket dependency plug set is outside its table.");
                    Members(sets, sharedRows[shared] + 8, owner);
                }
            }
        }
        if (result.Count != 0 && (!catalog.TryGetEntry(SocketDependencyManifest, out var manifest)
            || manifest.Reference != PackageManifest.ClassId))
            throw new InvalidDataException("The investment globals manifest is missing.");
        return result;

        void Members(byte[] bytes, int field, uint owner)
        {
            foreach (int row in SocketRows(bytes, field, 0x80802E03, 32)) Add(SocketU32(bytes, row), owner);
        }
        void Add(uint index, uint owner)
        {
            if (index is ushort.MaxValue or uint.MaxValue) return;
            if (index >= 0x8000 || index >= definitionRows.Length)
                throw new InvalidDataException("A socket dependency names an unreachable item index.");
            int row = definitionRows[index], display = displayRows[index];
            uint tag = SocketU32(definitions, row + 16);
            if (!authored.TryGetValue(tag, out var item)) return;
            uint displayTag = SocketU32(displays, display + 16);
            if (item.Class != SocketItemClass || SocketU32(definitions, row) != SocketU32(displays, display)
                || !authored.TryGetValue(displayTag, out var label) || label.Class != 0x80805CE1)
                throw new InvalidDataException("An authored socket plug has no matching definition and display.");
            if (result.TryAdd(tag, owner)) pending.Enqueue((tag, item.Data));
            result.TryAdd(displayTag, owner);
        }
    }

    /** Dependency indexes are read only from bounded arrays with the native element class. */
    private static int[] SocketRows(byte[] data, int field, uint type, int stride)
    {
        if (field < 0 || field > data.Length - 16)
            throw new InvalidDataException("A socket dependency array is truncated.");
        long count = SocketI64(data, field);
        if (count == 0) return [];
        long header = field + 8 + SocketI64(data, field + 8);
        if (count < 0 || count > 32768 || header < 4 || header > data.Length - 16
            || count > (data.Length - header - 16) / stride
            || SocketU32(data, (int)header - 4) != 0x80809FBD
            || SocketI64(data, (int)header) != count || SocketU32(data, (int)header + 8) != type)
            throw new InvalidDataException("A socket dependency array has an invalid native layout.");
        return Enumerable.Range(0, (int)count).Select(index => checked((int)header + 16 + index * stride)).ToArray();
    }

    private static ushort SocketU16(byte[] data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
    private static uint SocketU32(byte[] data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
    private static long SocketI64(byte[] data, int at) => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at));
}
