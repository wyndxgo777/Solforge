using System.Globalization;
using System.Numerics;

namespace SunriseStudio.Formats;

/** Occluder meshes in world space, three positions per triangle; each mesh fills the space it encloses. */
public sealed class TomeOcclusionEdit
{
    /** Meshes that no longer block; their voxels open unless another mesh also fills them. */
    public List<IReadOnlyList<Vector3>> Removed { get; init; } = [];
    /** Meshes that now block. */
    public List<IReadOnlyList<Vector3>> Added { get; init; } = [];
    /** Every other static and terrain mesh near the edit; it keeps its voxels solid. */
    public List<IReadOnlyList<Vector3>> Kept { get; init; } = [];
}

/** Summary of an edit: tiles touched and cells, portals and voxels added, cut, opened or closed. */
public sealed record TomeEditReport(int LeafTiles, int HierarchyTiles, int CellsAdded, int PortalsAdded, int PortalsCut,
    int ClusterPortalsAdded, long OpenedVoxels, long ClosedVoxels);

/**
 * Rebuilds the occlusion of the leaf tiles an edit touches, inside the edit region only, and updates
 * every structure that depends on them. Cells and clusters outside the region keep their numbers.
 */
public static partial class TomeEditor
{
    private const int MaximumTileCells = 2047;

    /** The voxel edge the tome was baked with, from its computation string. */
    public static float VoxelSize(Tome tome)
    {
        var parts = tome.ComputationString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int at = Array.IndexOf(parts, "SH");
        if (at < 0 || at + 1 >= parts.Length || !float.TryParse(parts[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float size) || !(size > 0))
            throw new InvalidDataException("The tome has no smallest-hole size.");
        return size;
    }

    // Lines a blocking test walks at most.
    private const int MaximumProbes = 20_000;

    /**
     * The share of axis lines through a mesh, empty voxel to empty voxel, that the tome blocks. A line
     * passes when each voxel on it has a cell and each change of cell has a portal there.
     * @param tome Tome to test.
     * @param mesh Three positions per triangle, world space.
     * @param kept Other meshes; a line that starts or ends in them says nothing about this mesh.
     * @return From 0 to 1; NaN when no line could be tested.
     */
    public static double BlockedShare(Tome tome, IReadOnlyList<Vector3> mesh, IEnumerable<IReadOnlyList<Vector3>> kept)
    {
        float size = VoxelSize(tome);
        var grid = new Grid(tome.Min, size);
        var box = TomeVoxelizer.Cover(mesh, tome.Min, size).Grow(2).Intersect(grid.Box(tome.Min, tome.Max));
        if (box.IsEmpty) return double.NaN;
        var own = new TomeVoxelGrid(box);
        var other = new TomeVoxelGrid(box);
        TomeVoxelizer.MarkSolid(own, mesh, tome.Min, size);
        foreach (var item in kept) TomeVoxelizer.MarkSolid(other, item, tome.Min, size);
        var context = new EditContext(tome, grid, new TileTreeIndex(tome));
        var snapshots = new HashSet<int>();
        var starts = new List<(int X, int Y, int Z, int Axis)>();
        for (int z = box.MinZ; z < box.MaxZ; z++)
            for (int y = box.MinY; y < box.MaxY; y++)
                for (int x = box.MinX; x < box.MaxX; x++)
                    for (int axis = 0; axis < 3; axis++)
                    {
                        var (px, py, pz) = Step(x, y, z, axis, -1);
                        if (own[x, y, z] && box.Contains(px, py, pz) && !own[px, py, pz] && !other[px, py, pz])
                            starts.Add((px, py, pz, axis));
                    }
        // Lines through a face's edge graze voxels the bake may not have filled; the inner lines decide.
        var set = starts.ToHashSet();
        var inner = starts.Where(s => Enumerable.Range(1, 2).All(k =>
        {
            int u = (s.Axis + k) % 3;
            var (ax, ay, az) = Step(s.X, s.Y, s.Z, u, 1);
            var (bx, by, bz) = Step(s.X, s.Y, s.Z, u, -1);
            return set.Contains((ax, ay, az, s.Axis)) && set.Contains((bx, by, bz, s.Axis));
        })).ToList();
        if (inner.Count > 0) starts = inner;
        int stride = Math.Max(1, starts.Count / MaximumProbes), blocked = 0, passed = 0;
        for (int i = 0; i < starts.Count; i += stride)
        {
            var (x, y, z, axis) = starts[i];
            var line = new List<(int X, int Y, int Z)> { (x, y, z) };
            do { (x, y, z) = Step(x, y, z, axis, 1); line.Add((x, y, z)); } while (box.Contains(x, y, z) && own[x, y, z]);
            if (!box.Contains(x, y, z) || other[x, y, z]) continue;
            var cells = line.Select(v => Cell(v.X, v.Y, v.Z)).ToList();
            if (cells[0].Cell < 0 || cells[^1].Cell < 0) continue;
            bool open = true;
            for (int k = 0; open && k + 1 < cells.Count; k++)
            {
                if (cells[k + 1].Cell < 0) { open = false; break; }
                if (cells[k] == cells[k + 1]) continue;
                var (fx, fy, fz) = line[k + 1];
                var face = grid.Center(fx, fy, fz);
                face = axis == 0 ? face with { X = grid.Corner(fx, fy, fz).X } : axis == 1
                    ? face with { Y = grid.Corner(fx, fy, fz).Y } : face with { Z = grid.Corner(fx, fy, fz).Z };
                if (snapshots.Add(cells[k].Slot)) context.Snapshot(cells[k].Slot);
                open = context.HadPortal(cells[k].Slot, cells[k].Cell, cells[k + 1].Slot, cells[k + 1].Cell, axis * 2 + 1, face);
            }
            if (open) passed++; else blocked++;
        }
        return blocked + passed == 0 ? double.NaN : (double)blocked / (blocked + passed);

        (int Slot, int Cell) Cell(int x, int y, int z)
        {
            int slot = context.SlotAt(x, y, z);
            return slot < 0 || tome.Tiles[slot] is not { IsLeaf: true } ? (-1, Outside) : (slot, context.OriginalCell(slot, x, y, z));
        }
    }

    private static (int X, int Y, int Z) Step(int x, int y, int z, int axis, int by) =>
        axis == 0 ? (x + by, y, z) : axis == 1 ? (x, y + by, z) : (x, y, z + by);

    /**
     * Applies an occluder edit in place.
     * @param tome Tome to change.
     * @param edit Removed, added and kept surfaces.
     * @return What changed.
     */
    public static TomeEditReport Apply(Tome tome, TomeOcclusionEdit edit)
    {
        float size = VoxelSize(tome);
        var grid = new Grid(tome.Min, size);
        var extent = grid.Box(tome.Min, tome.Max);
        var region = new TomeVoxelBox(0, 0, 0, 0, 0, 0);
        foreach (var mesh in edit.Removed.Concat(edit.Added)) region = region.Union(TomeVoxelizer.Cover(mesh, tome.Min, size));
        if (region.IsEmpty) return new(0, 0, 0, 0, 0, 0, 0, 0);
        region = region.Grow(1).Intersect(extent);
        var removed = new TomeVoxelGrid(region);
        var added = new TomeVoxelGrid(region);
        var kept = new TomeVoxelGrid(region);
        foreach (var mesh in edit.Removed) TomeVoxelizer.MarkSolid(removed, mesh, tome.Min, size);
        // The bake can block up to a voxel outside an occluder, so opening reaches one voxel further.
        removed = TomeVoxelizer.Dilate(removed);
        // New solid ends within half a voxel of the surface; the bake keeps space beside an occluder open.
        foreach (var mesh in edit.Added) TomeVoxelizer.MarkInside(added, mesh, tome.Min, size);
        foreach (var mesh in edit.Kept) TomeVoxelizer.MarkSolid(kept, mesh, tome.Min, size);

        var tree = new TileTreeIndex(tome);
        var context = new EditContext(tome, grid, tree);
        var near = region.Grow(2);
        foreach (int slot in tree.LeafSlots)
        {
            var tileBox = grid.TileBox(tome.Tiles[slot]!);
            if (!tileBox.Intersect(near).IsEmpty) context.Snapshot(slot);
            var box = tileBox.Intersect(region);
            if (!box.IsEmpty) context.Works[slot] = new TileWork(context, slot, box);
        }
        var works = context.Works.Values.OrderBy(work => work.Slot).ToList();
        int cells = tome.Tiles.Sum(tile => tile?.Cells.Count ?? 0);
        long opened = 0, closed = 0;
        foreach (var work in works)
        {
            var counts = work.Classify(removed, added, kept);
            opened += counts.Opened;
            closed += counts.Closed;
        }
        int portals = 0, clusterPortals = 0, cut = 0;
        // Opening adds portals only, so it runs on the original cells; closing then splits cells, portals included.
        foreach (var work in works) { var (p, c) = Carve(context, work); portals += p; clusterPortals += c; }
        foreach (var work in works) { work.PrepareSplit(); work.CreateSlabs(); }
        // Retarget first: a piece's entering portals decide what its view can see.
        cut += Retarget(context);
        foreach (var work in works) cut += Distribute(context, work);
        foreach (var work in works) portals += SlabPortals(context, work);
        foreach (var work in works) portals += CenterPortals(context, work);
        foreach (var work in works) CommitTree(context, work);
        int hierarchy = EditHierarchy(context, region, ref portals, ref cut);
        return new(works.Count, hierarchy, tome.Tiles.Sum(tile => tile?.Cells.Count ?? 0) - cells, portals, cut, clusterPortals, opened, closed);
    }

    /** Voxel grid anchored at the tome minimum. */
    private sealed class Grid(Vector3 origin, float size)
    {
        internal Vector3 Origin => origin;
        internal float Size => size;

        /** Converts a world-space box to a voxel box on the grid. */
        internal TomeVoxelBox Box(Vector3 min, Vector3 max)
        {
            var a = (min - origin) / size;
            var b = (max - origin) / size;
            return new((int)MathF.Floor(a.X + 1e-3f), (int)MathF.Floor(a.Y + 1e-3f), (int)MathF.Floor(a.Z + 1e-3f),
                (int)MathF.Ceiling(b.X - 1e-3f), (int)MathF.Ceiling(b.Y - 1e-3f), (int)MathF.Ceiling(b.Z - 1e-3f));
        }

        /** A tile's voxels; a tile off the bake grid is refused. */
        internal TomeVoxelBox TileBox(TomeTile tile)
        {
            var a = (tile.Min - origin) / size;
            var b = (tile.Max - origin) / size;
            foreach (float v in new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z })
                if (MathF.Abs(v - MathF.Round(v)) > 1e-3f) throw new NotSupportedException("A tile is not on the bake voxel grid.");
            return new((int)MathF.Round(a.X), (int)MathF.Round(a.Y), (int)MathF.Round(a.Z),
                (int)MathF.Round(b.X), (int)MathF.Round(b.Y), (int)MathF.Round(b.Z));
        }

        /** World position of a voxel corner. */
        internal Vector3 Corner(int x, int y, int z) => origin + new Vector3(x, y, z) * size;
        /** World position of a voxel center. */
        internal Vector3 Center(int x, int y, int z) => origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * size;
    }

    /** Leaf slots and the parent of every tile-tree node. */
    private sealed class TileTreeIndex
    {
        internal TileTreeIndex(Tome tome)
        {
            var codes = tome.TileTree.Codes;
            var ranks = tome.TileTree.Ranks();
            Parents = Enumerable.Repeat(-1, codes.Count).ToArray();
            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i] == TomeKdTree.Leaf) { if (tome.Tiles[i]?.IsLeaf == true) LeafSlots.Add(i); continue; }
                int right = ranks[i] << 1;
                Parents[right - 1] = i;
                Parents[right] = i;
            }
        }

        internal List<int> LeafSlots { get; } = [];
        internal int[] Parents { get; }

        /** Yields the tile's ancestors, parent first. */
        internal IEnumerable<int> Ancestors(int slot)
        {
            for (int node = Parents[slot]; node >= 0; node = Parents[node]) yield return node;
        }
    }
}
