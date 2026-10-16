using System.Numerics;

namespace SunriseStudio.Formats;

/** Opening voxels: removing solid geometry. */
public static partial class TomeEditor
{
    /**
     * Opens the space a removed occluder filled. Every cell in or around the opened box gets a portal
     * on each face of the box, facing out of it, into each cell just past that face.
     * @return Tile portals and cluster portals added.
     */
    private static (int Portals, int ClusterPortals) Carve(EditContext context, TileWork work)
    {
        var box = work.OpenedBox;
        if (box.IsEmpty) return (0, 0);
        var grid = context.Grid;
        var tile = work.Tile;
        var sources = new HashSet<int>();
        var shell = box.Grow(1).Intersect(work.TileVoxels);
        for (int z = shell.MinZ; z < shell.MaxZ; z++)
            for (int y = shell.MinY; y < shell.MaxY; y++)
                for (int x = shell.MinX; x < shell.MaxX; x++)
                    sources.UnionWith(work.GroupCells(x, y, z));
        Vector3 boxMin = grid.Corner(box.MinX, box.MinY, box.MinZ), boxMax = grid.Corner(box.MaxX, box.MaxY, box.MaxZ);
        foreach (int cell in Enumerable.Range(0, (int)box.Count).Select(i => Voxel(box, i))
            .Where(v => work.Opened(v.X, v.Y, v.Z)).Select(v => work.Label(v.X, v.Y, v.Z)).Where(c => c >= 0).Distinct().ToList())
        {
            context.AddObjects(tile.Cells[cell], boxMin, boxMax);
            context.GrowCluster((int)tile.Cells[cell].ClusterIndex, boxMin, boxMax);
        }
        int portals = 0, clusterPortals = 0;
        for (int axis = 0; axis < 3; axis++)
            for (int side = 0; side < 2; side++)
            {
                int plane = side == 1 ? box.Max(axis) : box.Min(axis), layer = side == 1 ? plane : plane - 1;
                var inner = Across(context, work, box, axis, side == 1 ? plane - 1 : plane, plane)
                    .Where(item => item.Slot == work.Slot && item.Cell >= 0).ToList();
                foreach (var (min, max, slot, cell) in Across(context, work, box, axis, layer, plane))
                {
                    if (cell < 0) continue;
                    uint targetCluster = context.Tome.Tiles[slot]!.Cells[cell].ClusterIndex;
                    foreach (int source in sources)
                    {
                        if (slot == work.Slot && source == cell) continue;
                        portals += context.AddPortal(work.Slot, tile.Cells[source], slot, cell, cell, axis * 2 + side, min, max);
                        int sourceCluster = (int)tile.Cells[source].ClusterIndex;
                        clusterPortals += context.EnsureClusterPortal(sourceCluster, (int)targetCluster, axis * 2 + side, min, max);
                        clusterPortals += context.EnsureClusterPortal((int)targetCluster, sourceCluster, axis * 2 + 1 - side, min, max);
                    }
                    // A cell past a tile edge is no source here, so it gets its own way into the opened space.
                    if (slot == work.Slot) continue;
                    foreach (int target in inner.Where(item => Overlap(item.Min - Pad(axis), item.Max + Pad(axis), min - Pad(axis), max + Pad(axis)))
                        .Select(item => item.Cell).Distinct())
                        portals += context.AddPortal(slot, context.Tome.Tiles[slot]!.Cells[cell], work.Slot, target, target, axis * 2 + 1 - side, min, max);
                }
            }
        return (portals, clusterPortals);
    }

    /** The cells just across one face of a box, grouped into world rectangles on the face plane. */
    private static IEnumerable<(Vector3 Min, Vector3 Max, int Slot, int Cell)> Across(EditContext context, TileWork work,
        TomeVoxelBox box, int axis, int layer, int plane)
    {
        int ua = (axis + 1) % 3, va = (axis + 2) % 3;
        int u0 = box.Min(ua), u1 = box.Max(ua), v0 = box.Min(va), v1 = box.Max(va);
        var keys = new (int Slot, int Cell)[(u1 - u0) * (v1 - v0)];
        for (int v = v0; v < v1; v++)
            for (int u = u0; u < u1; u++)
            {
                var p = new int[3];
                p[axis] = layer; p[ua] = u; p[va] = v;
                keys[(v - v0) * (u1 - u0) + (u - u0)] = LabelAt(context, work, p[0], p[1], p[2]);
            }
        foreach (var (a0, a1, b0, b1, key) in Rectangles(keys, u1 - u0, v1 - v0))
        {
            var lo = new int[3]; var hi = new int[3];
            lo[axis] = hi[axis] = plane;
            lo[ua] = u0 + a0; hi[ua] = u0 + a1; lo[va] = v0 + b0; hi[va] = v0 + b1;
            yield return (context.Grid.Corner(lo[0], lo[1], lo[2]), context.Grid.Corner(hi[0], hi[1], hi[2]), key.Slot, key.Cell);
        }
    }

    /** A voxel's tile and its cell before any split: opened voxels carry their new label. */
    private static (int Slot, int Cell) LabelAt(EditContext context, TileWork work, int x, int y, int z)
    {
        if (work.TileVoxels.Contains(x, y, z)) return (work.Slot, work.Label(x, y, z));
        int slot = context.SlotAt(x, y, z);
        if (slot < 0) return (-1, Outside);
        return (slot, context.Works.TryGetValue(slot, out var other) ? other.Label(x, y, z) : context.OriginalCell(slot, x, y, z));
    }

    // Face rectangles are flat on their axis; a small thickness lets two on one plane overlap.
    private static Vector3 Pad(int axis) => TomeKdTree.Set(Vector3.Zero, axis, 1e-3f);

    private static (int X, int Y, int Z) Voxel(TomeVoxelBox box, int index) =>
        (box.MinX + index % box.SizeX, box.MinY + index / box.SizeX % box.SizeY, box.MinZ + index / (box.SizeX * box.SizeY));
}
