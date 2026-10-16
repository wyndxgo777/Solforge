using System.Numerics;

namespace SunriseStudio.Formats;

/** Rebuilds the tile hierarchy after leaf tiles change. */
public static partial class TomeEditor
{
    /**
     * Hierarchy tiles have no view tree, so they are edited in place: opened space gets exit-face
     * portals between the cells around it, and newly solid voxels are cut out of portal rectangles.
     * @return The number of hierarchy tiles touched.
     */
    private static int EditHierarchy(EditContext context, TomeVoxelBox region, ref int portals, ref int cut)
    {
        var tome = context.Tome;
        var grid = context.Grid;
        var closed = new TomeVoxelGrid(region);
        var openedBox = new TomeVoxelBox(0, 0, 0, 0, 0, 0);
        bool anyClosed = false;
        foreach (var work in context.Works.Values)
        {
            openedBox = openedBox.Union(work.OpenedBox);
            var b = work.ClosedBox;
            if (b.IsEmpty) continue;
            for (int z = b.MinZ; z < b.MaxZ; z++)
                for (int y = b.MinY; y < b.MaxY; y++)
                    for (int x = b.MinX; x < b.MaxX; x++)
                        if (work.Closed(x, y, z)) { closed[x, y, z] = true; anyClosed = true; }
        }
        var regionMin = grid.Corner(region.MinX, region.MinY, region.MinZ);
        var regionMax = grid.Corner(region.MaxX, region.MaxY, region.MaxZ);
        int tiles = 0;
        for (int slot = 0; slot < tome.Tiles.Count; slot++)
        {
            if (tome.Tiles[slot] is not { IsLeaf: false } tile || !Overlap(tile.Min, tile.Max, regionMin, regionMax)) continue;
            tiles++;
            if (!openedBox.IsEmpty) portals += Bridge(grid, slot, tile, openedBox);
            if (anyClosed) cut += Cut(grid, tile, closed, p => !p.IsGate && !p.IsOutside);
        }
        // Leaf portals into hierarchy tiles stand in for far views of the same opening.
        if (anyClosed)
            foreach (var work in context.Works.Values) cut += Cut(grid, work.Tile, closed, p => p.IsHierarchy && !p.IsGate);
        return tiles;
    }

    /** For each face of the opened box, portals from every nearby cell to each cell just outside that face, facing out. */
    private static int Bridge(Grid grid, int slot, TomeTile tile, TomeVoxelBox opened)
    {
        var grown = opened.Grow(1);
        Vector3 grownMin = grid.Corner(grown.MinX, grown.MinY, grown.MinZ), grownMax = grid.Corner(grown.MaxX, grown.MaxY, grown.MaxZ);
        var near = Enumerable.Range(0, tile.Cells.Count).Where(c => Overlap(CellMin(tile, c), CellMax(tile, c), grownMin, grownMax)).ToList();
        int added = 0;
        for (int axis = 0; axis < 3; axis++)
            for (int side = 0; side < 2; side++)
            {
                int plane = side == 1 ? opened.Max(axis) : opened.Min(axis);
                var slab = axis switch
                {
                    0 => opened with { MinX = side == 1 ? plane : plane - 1, MaxX = side == 1 ? plane + 1 : plane },
                    1 => opened with { MinY = side == 1 ? plane : plane - 1, MaxY = side == 1 ? plane + 1 : plane },
                    _ => opened with { MinZ = side == 1 ? plane : plane - 1, MaxZ = side == 1 ? plane + 1 : plane }
                };
                Vector3 slabMin = grid.Corner(slab.MinX, slab.MinY, slab.MinZ), slabMax = grid.Corner(slab.MaxX, slab.MaxY, slab.MaxZ);
                float at = TomeKdTree.Get(grid.Origin, axis) + plane * grid.Size;
                foreach (int target in near.Where(c => Overlap(CellMin(tile, c), CellMax(tile, c), slabMin, slabMax)))
                {
                    var min = TomeKdTree.Set(Vector3.Max(slabMin, CellMin(tile, target)), axis, at);
                    var max = TomeKdTree.Set(Vector3.Min(slabMax, CellMax(tile, target)), axis, at);
                    foreach (int source in near.Where(c => c != target))
                    {
                        var portal = Make(tile, axis * 2 + side, (uint)slot, target, min, max, hierarchy: true);
                        if (tile.Cells[source].Portals.Contains(portal)) continue;
                        tile.Cells[source].Portals.Add(portal);
                        added++;
                    }
                }
            }
        return added;
    }

    /** Removes the parts of portal rectangles that a newly solid voxel touches on either side of the plane. */
    private static int Cut(Grid grid, TomeTile tile, TomeVoxelGrid closed, Func<TomePortal, bool> applies)
    {
        int cut = 0;
        foreach (var cell in tile.Cells)
        {
            var result = new List<TomePortal>(cell.Portals.Count);
            foreach (var portal in cell.Portals)
            {
                var pieces = applies(portal) ? CutPortal(grid, tile, closed, portal) : null;
                if (pieces is null) { result.Add(portal); continue; }
                cut++;
                result.AddRange(pieces);
            }
            cell.Portals.Clear();
            cell.Portals.AddRange(result);
        }
        return cut;
    }

    /** The uncovered pieces of one portal, or null when no newly solid voxel touches it. */
    private static List<TomePortal>? CutPortal(Grid grid, TomeTile tile, TomeVoxelGrid closed, TomePortal portal)
    {
        int axis = portal.Axis, xa = (1 << axis) & 3, ya = (1 << xa) & 3;
        var scale = (tile.Max - tile.Min) / 65535f;
        float ToVoxel(float gridValue, int a) => (TomeKdTree.Get(tile.Min, a) + gridValue * TomeKdTree.Get(scale, a) - TomeKdTree.Get(grid.Origin, a)) / grid.Size;
        float ToGridValue(int voxel, int a) => (TomeKdTree.Get(grid.Origin, a) + voxel * grid.Size - TomeKdTree.Get(tile.Min, a)) / TomeKdTree.Get(scale, a);
        float plane = ToVoxel(portal.Z, axis);
        int rounded = (int)MathF.Round(plane);
        int[] layers = MathF.Abs(plane - rounded) < 1e-3f ? [rounded - 1, rounded] : [(int)MathF.Floor(plane)];
        if (layers.All(layer => layer < closed.Box.Min(axis) || layer >= closed.Box.Max(axis))) return null;
        int i0 = (int)MathF.Floor(ToVoxel(portal.MinX, xa)), i1 = Math.Max(i0 + 1, (int)MathF.Ceiling(ToVoxel(portal.MaxX, xa)));
        int j0 = (int)MathF.Floor(ToVoxel(portal.MinY, ya)), j1 = Math.Max(j0 + 1, (int)MathF.Ceiling(ToVoxel(portal.MaxY, ya)));
        if (i1 <= closed.Box.Min(xa) || i0 >= closed.Box.Max(xa) || j1 <= closed.Box.Min(ya) || j0 >= closed.Box.Max(ya)) return null;
        int w = i1 - i0, h = j1 - j0;
        if ((long)w * h > 16_000_000) throw new NotSupportedException("A hierarchy portal is too large to cut.");
        var blocked = new bool[w * h];
        bool any = false;
        var p = new int[3];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
                foreach (int layer in layers)
                {
                    p[axis] = layer; p[xa] = i0 + i; p[ya] = j0 + j;
                    if (!closed[p[0], p[1], p[2]]) continue;
                    blocked[j * w + i] = any = true;
                    break;
                }
        if (!any) return null;
        var pieces = new List<TomePortal>();
        foreach (var (a0, a1, b0, b1, isBlocked) in Rectangles(blocked, w, h))
        {
            if (isBlocked) continue;
            int x0 = Math.Max(portal.MinX, (int)MathF.Floor(ToGridValue(i0 + a0, xa))), x1 = Math.Min(portal.MaxX, (int)MathF.Ceiling(ToGridValue(i0 + a1, xa)));
            int y0 = Math.Max(portal.MinY, (int)MathF.Floor(ToGridValue(j0 + b0, ya))), y1 = Math.Min(portal.MaxY, (int)MathF.Ceiling(ToGridValue(j0 + b1, ya)));
            if (x0 <= x1 && y0 <= y1) pieces.Add(portal with { XRange = (uint)x0 << 16 | (uint)x1, YRange = (uint)y0 << 16 | (uint)y1 });
        }
        return pieces;
    }
}
