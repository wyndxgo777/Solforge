using System.Buffers.Binary;
using System.Text;

namespace SunriseStudio.Formats;

/** Encodes a format 18 tome; every count, offset, lookup table and the CRC come from the model. */
public static class TomeWriter
{
    /**
     * Encodes a tome in the optimizer's block order, each block 16-byte aligned and zero padded.
     * @param tome The model.
     * @return The tome bytes with a fresh CRC.
     */
    public static byte[] Write(Tome tome)
    {
        int nodes = tome.TileTree.Codes.Count;
        if (tome.Tiles.Count != nodes || tome.LodLevels.Count != nodes)
            throw new InvalidDataException("Tiles and LOD levels need one entry per tile-tree node.");
        var objectWidths = tome.ObjectListWidthOverride ?? ListWidths(tome.ObjectLists);
        var clusterWidths = tome.ClusterListWidthOverride ?? ListWidths(tome.ClusterLists);
        int pathBits = tome.SlotPathBitsOverride ?? Depth(tome.TileTree);
        var output = new Blocks(TomeReader.HeaderSize);
        int tree = output.Add(Words(TomeBits.KdData(tome.TileTree.Codes)));
        int splits = output.Add(Floats(tome.TileTree.Splits));
        int paths = output.Add(SlotPaths(tome.TileTree, pathBits));
        int lods = output.Add(Floats(tome.LodLevels));
        int starts = output.Add(Ints(tome.CellStarts()));
        int objectLists = output.Add(Runs(tome.ObjectLists, objectWidths));
        int clusterLists = output.Add(Runs(tome.ClusterLists, clusterWidths));
        var clusterNodes = new List<byte>();
        var clusterPortals = new List<byte>();
        uint clusterPortalIndex = 0;
        foreach (var cluster in tome.Clusters)
        {
            var (a, b, c) = cluster.Bounds.Pack();
            clusterNodes.AddRange(Words([clusterPortalIndex, (uint)cluster.Portals.Count, a, b, c]));
            foreach (var portal in cluster.Portals) clusterPortals.AddRange(Portal(portal));
            clusterPortalIndex += (uint)cluster.Portals.Count;
        }
        int clusters = output.Add(clusterNodes.ToArray());
        int clusterPortalBlock = output.Add(clusterPortals.ToArray());
        int gateVertices = output.Add(Floats(tome.GateVertices.SelectMany(v => new[] { v.X, v.Y, v.Z })));
        int gateIndices = output.Add(Ints(tome.GateIndices));
        int objects = output.Add(Floats(tome.Objects.SelectMany(o => new[] { o.Min.X, o.Min.Y, o.Min.Z, o.Max.X, o.Max.Y, o.Max.Z })));
        int userStarts = 0;
        IEnumerable<uint> flatIds;
        if (tome.UserIdGroups is { } groups)
        {
            if (groups.Count != tome.Objects.Count) throw new InvalidDataException("Each object needs one user id group.");
            var prefix = new uint[groups.Count + 1];
            for (int i = 0; i < groups.Count; i++) prefix[i + 1] = checked(prefix[i] + (uint)groups[i].Count);
            userStarts = output.Add(Words(prefix));
            flatIds = groups.SelectMany(group => group);
        }
        else flatIds = tome.UserIds ?? throw new InvalidDataException("The tome has no user ids.");
        int userIds = output.Add(Words(flatIds.ToArray()));
        int tileTable = output.Add(new byte[4 * nodes]);
        var tilePointers = new uint[nodes];
        for (int i = 0; i < nodes; i++) tilePointers[i] = tome.Tiles[i] is { } tile ? (uint)output.Add(TileBytes(tile)) : 0;
        if (nodes != 0) output.Replace(tileTable, Words(tilePointers));
        int gateMap = output.Add(Words(tome.GateIds.ToArray()));
        int total = output.Position;

        var bytes = new byte[total];
        void U(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        void F(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
        U(0x00, (uint)Tome.Magic << 16 | Tome.Version);
        U(0x08, (uint)total);
        F(0x0C, tome.LodBaseDistance);
        U(0x10, tome.Flags);
        F(0x14, tome.Min.X); F(0x18, tome.Min.Y); F(0x1C, tome.Min.Z);
        F(0x20, tome.Max.X); F(0x24, tome.Max.Y); F(0x28, tome.Max.Z);
        U(0x2C, (uint)nodes << 5); U(0x30, (uint)tree); U(0x38, (uint)tome.TileTree.Splits.Count); U(0x3C, (uint)splits);
        U(0x40, (uint)tome.Objects.Count); U(0x44, (uint)objects); U(0x4C, (uint)userStarts); U(0x50, (uint)userIds);
        U(0x54, (uint)(objectWidths.Value | objectWidths.Count << 5 | clusterWidths.Value << 10 | clusterWidths.Count << 15));
        U(0x58, (uint)objectLists); U(0x5C, (uint)tome.ObjectLists.Count);
        U(0x60, (uint)clusterLists); U(0x64, (uint)tome.ClusterLists.Count);
        U(0x68, (uint)tome.GateIds.Count); U(0x6C, (uint)gateMap); U(0x70, (uint)gateVertices);
        U(0x74, (uint)tome.GateVertices.Count); U(0x78, (uint)gateIndices);
        U(0x7C, (uint)tome.Clusters.Count); U(0x80, (uint)clusters); U(0x84, (uint)clusterPortalBlock); U(0x88, (uint)starts);
        U(0x8C, (uint)(tome.LeafTileCountOverride ?? tome.Tiles.Count(tile => tile?.IsLeaf == true)));
        U(0x90, (uint)nodes); U(0x94, (uint)pathBits); U(0x98, (uint)paths); U(0x9C, (uint)lods); U(0xA0, (uint)tileTable);
        var text = Encoding.ASCII.GetBytes(tome.ComputationString);
        if (text.Length > 128 || tome.ComputationString.Any(c => c > 0x7F))
            throw new InvalidDataException("The computation string needs at most 128 ASCII bytes.");
        text.CopyTo(bytes.AsSpan(0xBC));
        output.CopyTo(bytes);
        U(0x04, TomeBits.Crc32C(bytes.AsSpan(8)));
        return bytes;
    }

    /** The tile header, then cells, portals, planes, view tree, split values, cell map and BSP nodes. */
    private static byte[] TileBytes(TomeTile tile)
    {
        var output = new Blocks(TomeReader.TileHeaderSize);
        var cells = new List<byte>();
        var portals = new List<byte>();
        uint portalIndex = 0;
        foreach (var cell in tile.Cells)
        {
            var (a, b, c) = cell.Bounds.Pack();
            cells.AddRange(Words([portalIndex, (uint)cell.Portals.Count, cell.ObjectIndex, cell.ObjectCount,
                cell.ClusterIndex, cell.ClusterCount, a, b, c]));
            foreach (var portal in cell.Portals) portals.AddRange(Portal(portal));
            portalIndex += (uint)cell.Portals.Count;
        }
        int cellBlock = output.Add(cells.ToArray());
        int portalBlock = output.Add(portals.ToArray());
        int planes = output.Add(Floats(tile.Planes.SelectMany(p => new[] { p.X, p.Y, p.Z, p.W })));
        int tree = 0, splits = 0, map = 0, nodes = 0, width = 0;
        if (tile.ViewTree is { } view)
        {
            nodes = view.Codes.Count;
            width = tile.MapWidthOverride ?? tile.DerivedMapWidth();
            tree = output.Add(Words(TomeBits.KdData(view.Codes)));
            splits = output.Add(Floats(view.Splits));
            if (tile.CellMap.Count != (width == 0 ? 0 : view.LeafCount))
                throw new InvalidDataException("The cell map needs one entry per view-tree leaf.");
            var bits = new TomeBitWriter();
            foreach (int entry in tile.CellMap) bits.Put(MapValue(entry, width), width);
            map = output.Add(bits.ToBytes());
        }
        else if (tile.CellMap.Count != 0 || tile.MapWidthOverride is > 0)
            throw new InvalidDataException("A cell map needs a view tree.");
        var bsp = new List<uint>();
        foreach (var node in tile.Bsp)
        {
            if (node.Plane > 0x3FFFFFFF) throw new InvalidDataException("A BSP plane index does not fit.");
            bsp.Add(node.Plane | (node.FrontIsLeaf ? 1u << 31 : 0) | (node.BackIsLeaf ? 1u << 30 : 0));
            bsp.Add((uint)node.Front << 16 | node.Back);
        }
        int bspBlock = output.Add(Words(bsp.ToArray()));
        int size = output.Position;
        int clusters = tile.ClusterCountOverride ?? tile.DerivedClusterCount();
        if (tile.Cells.Count > 0xFFFF || clusters > 0xFFFF || size >= 1 << 24 || tile.Flags > 0xFF)
            throw new InvalidDataException("The tile does not fit its header.");
        var bytes = new byte[size];
        void U(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        void F(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value);
        F(0x00, tile.Min.X); F(0x04, tile.Min.Y); F(0x08, tile.Min.Z);
        F(0x0C, tile.Max.X); F(0x10, tile.Max.Y); F(0x14, tile.Max.Z);
        U(0x18, (uint)(nodes << 5 | width)); U(0x1C, (uint)tree); U(0x20, (uint)map);
        U(0x24, (uint)(tile.ViewTree?.Splits.Count ?? 0)); U(0x28, (uint)splits);
        U(0x2C, (uint)size << 8 | tile.Flags);
        F(0x30, tile.PortalExpand);
        U(0x34, (uint)(clusters << 16 | tile.Cells.Count));
        U(0x38, (uint)cellBlock); U(0x3C, (uint)portalBlock); U(0x40, (uint)bspBlock); U(0x44, (uint)tile.Bsp.Count);
        U(0x48, (uint)planes); U(0x4C, (uint)tile.Planes.Count);
        output.CopyTo(bytes);
        return bytes;
    }

    private static uint MapValue(int entry, int width)
    {
        if (width == 0) throw new InvalidDataException("A cell map entry needs a width.");
        uint top = 1u << width - 1;
        if (entry == TomeTile.NoCell) return width == 32 ? uint.MaxValue : (1u << width) - 1;
        if (entry < 0)
        {
            int node = TomeTile.BspNodeOf(entry);
            if (width < 2 || (uint)node >= top - 1) throw new InvalidDataException($"BSP node {node} does not fit a {width}-bit cell map.");
            return top | (uint)node;
        }
        if ((uint)entry >= top) throw new InvalidDataException($"Cell {entry} does not fit a {width}-bit cell map.");
        return (uint)entry;
    }

    /** Value and count widths of a run list: the bit length of each largest, at least 1; empty lists are zero. */
    internal static (int Value, int Count) ListWidths(IReadOnlyList<TomeRun> runs) => runs.Count == 0 ? (0, 0)
        : (Math.Max(1, TomeBits.Length(runs.Max(run => run.Value))), Math.Max(1, TomeBits.Length(runs.Max(run => run.Count))));

    /** The tile-tree depth in levels; the root alone is one. */
    internal static int Depth(TomeKdTree tree)
    {
        if (tree.Codes.Count == 0) return 0;
        var ranks = tree.Ranks();
        int best = 0;
        var stack = new Stack<(int Node, int Depth)>();
        stack.Push((0, 1));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            best = Math.Max(best, depth);
            if (tree.Codes[node] == TomeKdTree.Leaf) continue;
            int right = ranks[node] << 1;
            stack.Push((right - 1, depth + 1));
            stack.Push((right, depth + 1));
        }
        return best;
    }

    /** One field per node: a marker bit at its depth, with the left turns above it. */
    internal static byte[] SlotPaths(TomeKdTree tree, int bits)
    {
        var values = new ulong[tree.Codes.Count];
        if (values.Length != 0)
        {
            var ranks = tree.Ranks();
            var stack = new Stack<(int Node, int Level, ulong Path)>();
            stack.Push((0, 0, 0));
            while (stack.Count > 0)
            {
                var (node, level, path) = stack.Pop();
                if (level >= bits || bits > 63) throw new InvalidDataException($"Slot path depth {level} does not fit {bits} bits.");
                values[node] = 1UL << bits - level - 1 | path << bits - level;
                if (tree.Codes[node] == TomeKdTree.Leaf) continue;
                int right = ranks[node] << 1;
                stack.Push((right, level + 1, path));
                stack.Push((right - 1, level + 1, path | 1UL << level));
            }
        }
        var writer = new TomeBitWriter();
        foreach (ulong value in values) writer.Put(value, bits);
        return writer.ToBytes();
    }

    private static byte[] Runs(IReadOnlyList<TomeRun> runs, (int Value, int Count) widths)
    {
        var writer = new TomeBitWriter();
        foreach (var run in runs) { writer.Put(run.Value, widths.Value); writer.Put(run.Count, widths.Count); }
        return writer.ToBytes();
    }

    private static byte[] Portal(TomePortal portal) => Words([portal.Link, portal.IndexZ, portal.XRange, portal.YRange]);

    private static byte[] Words(IReadOnlyList<uint> values)
    {
        var bytes = new byte[values.Count * 4];
        for (int i = 0; i < values.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    private static byte[] Ints(IReadOnlyList<int> values) => Words(values.Select(value => (uint)value).ToArray());

    private static byte[] Floats(IEnumerable<float> values)
    {
        var list = values.ToArray();
        var bytes = new byte[list.Length * 4];
        for (int i = 0; i < list.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), list[i]);
        return bytes;
    }

    /** Places blocks after a fixed header; an empty block takes pointer 0 and no bytes. */
    private sealed class Blocks(int start)
    {
        private readonly List<(int Offset, byte[] Data)> blocks = [];
        internal int Position { get; private set; } = start;

        /** Appends a data block aligned to 16 bytes and returns its offset; empty data returns 0. */
        internal int Add(byte[] data)
        {
            if (data.Length == 0) return 0;
            int offset = Position;
            blocks.Add((offset, data));
            Position = checked(offset + (data.Length + 15 & ~15));
            return offset;
        }

        /** Swaps the contents of an existing block for data of the same length. */
        internal void Replace(int offset, byte[] data)
        {
            int index = blocks.FindIndex(block => block.Offset == offset);
            if (index < 0 || blocks[index].Data.Length != data.Length) throw new InvalidOperationException("The block to replace is missing.");
            blocks[index] = (offset, data);
        }

        /** Copies every block to its offset in the target. */
        internal void CopyTo(Span<byte> target)
        {
            foreach (var (offset, data) in blocks) data.CopyTo(target[offset..]);
        }
    }
}
