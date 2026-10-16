using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace SunriseStudio.Formats;

/** Decodes an Umbra format 18 tome; anything it cannot rebuild exactly is refused. */
public static class TomeReader
{
    internal const int HeaderSize = 0x140, TileHeaderSize = 0x50, CellSize = 36, PortalSize = 16, ClusterSize = 20;

    /** True for bytes that start like a format 18 tome. */
    public static bool IsTome(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(data) == ((uint)Tome.Magic << 16 | Tome.Version);

    /**
     * Decodes a tome.
     * @param data The whole tome.
     * @return The typed model; derived fields that match their rule are left for the writer.
     */
    public static Tome Read(ReadOnlyMemory<byte> data)
    {
        var r = new Reader(data);
        if (!IsTome(data.Span)) throw new InvalidDataException("This is not an Umbra format 18 tome.");
        uint H(int offset) => r.U32(offset);
        if (H(8) != data.Length) throw new InvalidDataException("The tome size field differs from its length.");
        if (H(0x10) != 0) throw new InvalidDataException("Tome flags are not supported.");
        uint treeWord = H(0x2C);
        if ((treeWord & 31) != 0 || H(0x34) != 0) throw new InvalidDataException("The tile tree has a cell map.");
        int nodes = (int)(treeWord >> 5);
        var tome = new Tome
        {
            LodBaseDistance = r.F32(0x0C), Min = r.V3(0x14), Max = r.V3(0x20),
            TileTree = ReadTree(r, 0, H(0x30), nodes, H(0x38), H(0x3C), "tile tree")
        };
        if (H(0x48) != 0) throw new InvalidDataException("Object distances are not supported.");
        int objects = r.Count(H(0x40));
        for (int i = 0; i < objects; i++)
            tome.Objects.Add(new(r.V3(r.At(H(0x44), i, 24)), r.V3(r.At(H(0x44), i, 24) + 12)));
        if (H(0x4C) != 0)
        {
            var starts = new int[objects + 1];
            for (int i = 0; i <= objects; i++) starts[i] = checked((int)r.U32(r.At(H(0x4C), i, 4)));
            if (starts[0] != 0) throw new InvalidDataException("User id starts do not begin at zero.");
            tome.UserIdGroups = [];
            for (int i = 0; i < objects; i++)
            {
                if (starts[i] > starts[i + 1]) throw new InvalidDataException("User id starts are not sorted.");
                var group = new List<uint>(starts[i + 1] - starts[i]);
                for (int k = starts[i]; k < starts[i + 1]; k++) group.Add(r.U32(r.At(H(0x50), k, 4)));
                tome.UserIdGroups.Add(group);
            }
        }
        else
        {
            tome.UserIds = [];
            for (int i = 0; i < objects; i++) tome.UserIds.Add(r.U32(r.At(H(0x50), i, 4)));
        }
        uint widths = H(0x54);
        if (widths >> 20 != 0) throw new InvalidDataException("The list width word has unknown bits.");
        (int, int) objectWidths = ((int)(widths & 31), (int)(widths >> 5 & 31));
        (int, int) clusterWidths = ((int)(widths >> 10 & 31), (int)(widths >> 15 & 31));
        tome.ObjectLists.AddRange(ReadRuns(r, H(0x58), H(0x5C), objectWidths));
        tome.ClusterLists.AddRange(ReadRuns(r, H(0x60), H(0x64), clusterWidths));
        int gates = r.Count(H(0x68));
        if (gates != 0 && H(0x6C) == 0) throw new InvalidDataException("Gates have no index map.");
        for (int i = 0; i < gates; i++) tome.GateIds.Add(r.U32(r.At(H(0x6C), i, 4)));
        int vertices = r.Count(H(0x74));
        if (vertices != 0 && H(0x70) == 0) throw new InvalidDataException("Gate vertices have no pointer.");
        for (int i = 0; i < vertices; i++) tome.GateVertices.Add(r.V3(r.At(H(0x70), i, 12)));
        int clusters = r.Count(H(0x7C));
        uint clusterPortals = 0;
        for (int i = 0; i < clusters; i++)
        {
            int at = r.At(H(0x80), i, ClusterSize);
            if (r.U32(at) != clusterPortals) throw new InvalidDataException($"Cluster {i} portals are not contiguous.");
            int count = r.Count(r.U32(at + 4));
            var cluster = new TomeCluster { Bounds = TomeGridBox.Unpack(r.U32(at + 8), r.U32(at + 12), r.U32(at + 16)) };
            for (int k = 0; k < count; k++) cluster.Portals.Add(r.Portal(r.At(H(0x84), (int)clusterPortals + k, PortalSize)));
            clusterPortals += (uint)count;
            tome.Clusters.Add(cluster);
        }
        foreach (int offset in new[] { 0xA4, 0xA8, 0xAC, 0xB0, 0xB4, 0xB8, 0x13C })
            if (H(offset) != 0) throw new InvalidDataException($"Tome field 0x{offset:X} is not supported.");
        if (H(0x90) != nodes) throw new InvalidDataException("The tile count differs from the tile tree.");
        for (int i = 0; i < nodes; i++)
        {
            uint pointer = r.U32(r.At(H(0xA0), i, 4));
            tome.Tiles.Add(pointer == 0 ? null : ReadTile(r, checked((int)pointer), $"tile {i}"));
        }
        for (int i = 0; i < nodes; i++) tome.LodLevels.Add(r.F32(r.At(H(0x9C), i, 4)));
        if (H(0x78) != 0)
        {
            int length = AllPortals(tome).Where(portal => portal.IsGate)
                .Select(portal => portal.GateIndexOffset + portal.GateIndexCount).DefaultIfEmpty(0).Max();
            for (int i = 0; i < length; i++) tome.GateIndices.Add(r.I32(r.At(H(0x78), i, 4)));
        }
        var text = data.Span.Slice(0xBC, 128);
        int end = text.IndexOf((byte)0);
        if (end < 0) end = text.Length;
        if (text[end..].ContainsAnyExcept((byte)0)) throw new InvalidDataException("The computation string is not zero padded.");
        tome.ComputationString = Encoding.ASCII.GetString(text[..end]);

        var derivedStarts = tome.CellStarts();
        if (H(0x88) != 0)
            for (int i = 0; i <= nodes; i++)
                if (r.I32(r.At(H(0x88), i, 4)) != derivedStarts[i]) throw new InvalidDataException("Cell starts are not the running cell sum.");
        int pathBits = (int)H(0x94);
        if (H(0x98) != 0 && !TomeWriter.SlotPaths(tome.TileTree, pathBits).AsSpan()
            .SequenceEqual(r.Slice(checked((int)H(0x98)), TomeBits.Words((long)nodes * pathBits) * 4)))
            throw new InvalidDataException("Slot paths differ from the tile tree.");
        if (objectWidths != TomeWriter.ListWidths(tome.ObjectLists)) tome.ObjectListWidthOverride = objectWidths;
        if (clusterWidths != TomeWriter.ListWidths(tome.ClusterLists)) tome.ClusterListWidthOverride = clusterWidths;
        int leafTiles = (int)H(0x8C);
        if (leafTiles != tome.Tiles.Count(tile => tile?.IsLeaf == true)) tome.LeafTileCountOverride = leafTiles;
        if (pathBits != TomeWriter.Depth(tome.TileTree)) tome.SlotPathBitsOverride = pathBits;
        return tome;
    }

    /** Every portal of the tome: cluster portals and those of each tile's cells. */
    internal static IEnumerable<TomePortal> AllPortals(Tome tome) =>
        tome.Clusters.SelectMany(cluster => cluster.Portals)
            .Concat(tome.Tiles.Where(tile => tile is not null).SelectMany(tile => tile!.Cells.SelectMany(cell => cell.Portals)));

    private static TomeTile ReadTile(Reader r, int t, string name)
    {
        uint W(int offset) => r.U32(t + offset);
        uint flags = W(0x2C) & 0xFF;
        if ((flags & ~(TomeTile.LeafFlag | TomeTile.EmptyFlag)) != 0) throw new InvalidDataException($"{name}: tile flags are not supported.");
        var tile = new TomeTile { Min = r.V3(t), Max = r.V3(t + 12), Flags = flags, PortalExpand = r.F32(t + 0x30) };
        int cells = (int)(W(0x34) & 0xFFFF), storedClusters = (int)(W(0x34) >> 16);
        uint cellsAt = W(0x38), portalsAt = W(0x3C), bspAt = W(0x40), bspCount = W(0x44), planesAt = W(0x48), planeCount = W(0x4C);
        if (cells != 0 && cellsAt == 0) throw new InvalidDataException($"{name}: cells have no pointer.");
        uint portals = 0;
        for (int i = 0; i < cells; i++)
        {
            int at = r.At(t + (int)cellsAt, i, CellSize);
            if (r.U32(at) != portals) throw new InvalidDataException($"{name}: cell {i} portals are not contiguous.");
            int count = r.Count(r.U32(at + 4));
            if (count != 0 && portalsAt == 0) throw new InvalidDataException($"{name}: portals have no pointer.");
            var cell = new TomeCell
            {
                ObjectIndex = r.U32(at + 8), ObjectCount = r.U32(at + 12), ClusterIndex = r.U32(at + 16),
                ClusterCount = r.U32(at + 20), Bounds = TomeGridBox.Unpack(r.U32(at + 24), r.U32(at + 28), r.U32(at + 32))
            };
            for (int k = 0; k < count; k++) cell.Portals.Add(r.Portal(r.At(t + (int)portalsAt, (int)portals + k, PortalSize)));
            portals += (uint)count;
            tile.Cells.Add(cell);
        }
        uint treeWord = W(0x18);
        int nodes = (int)(treeWord >> 5), width = (int)(treeWord & 31);
        if (nodes != 0)
        {
            if (W(0x1C) == 0) throw new InvalidDataException($"{name}: the view tree has no pointer.");
            tile.ViewTree = ReadTree(r, t, W(0x1C), nodes, W(0x24), W(0x28), name);
            int leaves = tile.ViewTree.LeafCount;
            long bits = (long)leaves * width;
            if (bits != 0)
            {
                if (W(0x20) == 0) throw new InvalidDataException($"{name}: the cell map has no pointer.");
                var map = r.Slice(t + (int)W(0x20), TomeBits.Words(bits) * 4);
                uint top = 1u << width - 1, none = width == 32 ? uint.MaxValue : (1u << width) - 1;
                for (int i = 0; i < leaves; i++)
                {
                    uint value = TomeBits.Get(map, (long)i * width, width);
                    tile.CellMap.Add(value == none ? TomeTile.NoCell
                        : (value & top) != 0 ? TomeTile.BspReference((int)(value & top - 1)) : (int)value);
                }
            }
            else if (W(0x20) != 0) throw new InvalidDataException($"{name}: an empty cell map has a pointer.");
        }
        else if (W(0x1C) != 0 || W(0x20) != 0 || W(0x24) != 0 || W(0x28) != 0 || width != 0)
            throw new InvalidDataException($"{name}: view tree fields without a tree.");
        if ((bspAt == 0) != (bspCount == 0) || (planesAt == 0) != (planeCount == 0))
            throw new InvalidDataException($"{name}: BSP or plane pointer and count disagree.");
        for (int i = 0; i < r.Count(bspCount); i++)
        {
            int at = r.At(t + (int)bspAt, i, 8);
            uint a = r.U32(at), b = r.U32(at + 4);
            tile.Bsp.Add(new(a & 0x3FFFFFFF, (a >> 31) != 0, (a >> 30 & 1) != 0, (ushort)(b >> 16), (ushort)b));
        }
        for (int i = 0; i < r.Count(planeCount); i++)
        {
            int at = r.At(t + (int)planesAt, i, 16);
            tile.Planes.Add(new(r.F32(at), r.F32(at + 4), r.F32(at + 8), r.F32(at + 12)));
        }
        if (storedClusters != tile.DerivedClusterCount()) tile.ClusterCountOverride = storedClusters;
        if (width != tile.DerivedMapWidth()) tile.MapWidthOverride = width;
        return tile;
    }

    private static TomeKdTree ReadTree(Reader r, int baseOffset, uint data, int nodes, uint splitCount, uint splits, string name)
    {
        if (splitCount == 0 && splits != 0) throw new InvalidDataException($"{name}: a split pointer has no values.");
        int words = TomeBits.KdDataWords(nodes);
        var stored = r.Slice(checked(baseOffset + (int)data), words * 4);
        var tree = new TomeKdTree();
        for (int i = 0; i < nodes; i++) tree.Codes.Add((byte)TomeBits.Get(stored, 2L * i, 2));
        for (int i = 0; i < r.Count(splitCount); i++) tree.Splits.Add(r.F32(r.At(baseOffset + (int)splits, i, 4)));
        var rebuilt = TomeBits.KdData(tree.Codes);
        for (int i = 0; i < words; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(stored[(i * 4)..]) != rebuilt[i])
                throw new InvalidDataException($"{name}: the KD rank table differs from the rebuilt one.");
        return tree;
    }

    private static IEnumerable<TomeRun> ReadRuns(Reader r, uint at, uint count, (int Value, int Count) widths)
    {
        if (count != 0 && at == 0) throw new InvalidDataException("A run list has no pointer.");
        int total = widths.Value + widths.Count, entries = r.Count(count);
        var bits = r.Slice(checked((int)at), TomeBits.Words((long)entries * total) * 4);
        var runs = new TomeRun[entries];
        for (int i = 0; i < entries; i++)
            runs[i] = new(TomeBits.Get(bits, (long)i * total, widths.Value), TomeBits.Get(bits, (long)i * total + widths.Value, widths.Count));
        return runs;
    }

    /** Bounds-checked little-endian reads; any read outside the tome is invalid data. */
    private sealed class Reader(ReadOnlyMemory<byte> memory)
    {
        /** Bytes at offset; throws if the range leaves the data. */
        internal ReadOnlySpan<byte> Slice(int offset, int length) =>
            offset >= 0 && length >= 0 && offset <= memory.Length - length
                ? memory.Span.Slice(offset, length) : throw new InvalidDataException($"Tome read at 0x{offset:X} leaves the data.");
        /** Reads a 32-bit unsigned value. */
        internal uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));
        /** Reads a 32-bit signed value. */
        internal int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Slice(offset, 4));
        /** Reads a 32-bit float. */
        internal float F32(int offset) => BinaryPrimitives.ReadSingleLittleEndian(Slice(offset, 4));
        /** Reads three floats as a vector. */
        internal Vector3 V3(int offset) => new(F32(offset), F32(offset + 4), F32(offset + 8));
        /** Reads a four-word portal record. */
        internal TomePortal Portal(int offset) => new(U32(offset), U32(offset + 4), U32(offset + 8), U32(offset + 12));
        /** Offset of element index at stride from a pointer; throws on a null pointer. */
        internal int At(uint pointer, int index, int stride) => At(checked((int)pointer), index, stride);
        /** Offset of element index at stride from a pointer; throws on a null pointer. */
        internal int At(int pointer, int index, int stride) =>
            pointer == 0 ? throw new InvalidDataException("A tome block has no pointer.") : checked(pointer + index * stride);
        /** Validates a count against the data size. */
        internal int Count(uint value) => value <= memory.Length ? (int)value : throw new InvalidDataException("A tome count exceeds its data.");
    }
}
