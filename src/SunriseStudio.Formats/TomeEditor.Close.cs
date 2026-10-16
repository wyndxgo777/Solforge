using System.Numerics;

namespace SunriseStudio.Formats;

/** Closing voxels: adding solid geometry. */
public static partial class TomeEditor
{
    private const float Nudge = 0.25f;

    /**
     * Moves each portal of a split cell to the piece it sits in. Every other piece keeps it as well
     * unless the new solid hides it from that whole piece, since a camera saw all of its cell's portals.
     */
    private static int Distribute(EditContext context, TileWork work)
    {
        int dropped = 0;
        foreach (int cell in work.Split)
        {
            var pieces = Pieces(context, work, cell);
            var portals = work.Tile.Cells[cell].Portals.ToList();
            work.Tile.Cells[cell].Portals.Clear();
            foreach (var portal in portals)
            {
                if (portal.IsGate)
                {
                    int gateOwner = GateOwner(context, work, cell, portal);
                    work.Tile.Cells[gateOwner].Portals.Add(portal);
                    var (gateMin, gateMax) = GateBox(context, portal);
                    foreach (var (other, min, max) in pieces)
                        if (other != gateOwner && !Hidden(context, work, min, max, gateMin, gateMax)) work.Tile.Cells[other].Portals.Add(portal);
                    continue;
                }
                // A whole portal keeps its rectangle; only the parts the new solid covers are cut away.
                var kept = CutPortal(context.Grid, work.Tile, work.ClosedGrid, portal) ?? [portal];
                if (kept.Count == 0) dropped++;
                foreach (var piece in kept)
                {
                    var box = PortalBox(work.Tile, piece);
                    int owner = Owner(context, work, cell, Side(work.Tile, piece, source: true));
                    foreach (var (other, min, max) in pieces)
                        if (other == owner || !Hidden(context, work, min, max, box.Min, box.Max)) work.Tile.Cells[other].Portals.Add(piece);
                }
            }
        }
        return dropped;
    }

    /**
     * Every piece a split cell became, with the box a view can start from inside it: the piece itself
     * and every portal rectangle that enters it, since traversal looks out from there too.
     */
    private static List<(int Cell, Vector3 Min, Vector3 Max)> Pieces(EditContext context, TileWork work, int cell)
    {
        var entries = new Dictionary<int, (Vector3 Min, Vector3 Max)>();
        var tome = context.Tome;
        for (int slot = 0; slot < tome.Tiles.Count; slot++)
        {
            if (tome.Tiles[slot] is not { } tile) continue;
            foreach (var source in tile.Cells)
                foreach (var portal in source.Portals)
                {
                    if (portal.IsOutside || portal.IsHierarchy || portal.Target != (uint)work.Slot) continue;
                    var box = portal.IsGate ? GateBox(context, portal) : PortalBox(tile, portal);
                    entries[portal.TargetCell] = entries.TryGetValue(portal.TargetCell, out var seen)
                        ? (Vector3.Min(seen.Min, box.Min), Vector3.Max(seen.Max, box.Max)) : box;
                }
        }
        return Regions(context, work, cell).Select(piece => entries.TryGetValue(piece.Cell, out var entry)
            ? (piece.Cell, Vector3.Min(piece.Min, entry.Min), Vector3.Max(piece.Max, entry.Max)) : piece).ToList();
    }

    /** Every piece a split cell became, with its own world box: its slabs and its centre boxes. */
    private static List<(int Cell, Vector3 Min, Vector3 Max)> Regions(EditContext context, TileWork work, int cell)
    {
        var (cmin, cmax) = work.SplitBounds[cell];
        var result = new List<(int, Vector3, Vector3)>();
        foreach (var ((parent, ix, iy, iz), id) in work.Slabs)
        {
            if (parent != cell) continue;
            var index = new[] { ix, iy, iz };
            var lo = new float[3]; var hi = new float[3];
            for (int a = 0; a < 3; a++)
            {
                lo[a] = index[a] == 0 ? TomeKdTree.Get(cmin, a) : Math.Max(TomeKdTree.Get(cmin, a), work.Plane(a, low: index[a] == 1));
                hi[a] = index[a] == 2 ? TomeKdTree.Get(cmax, a) : Math.Min(TomeKdTree.Get(cmax, a), work.Plane(a, low: index[a] == 0));
            }
            result.Add((id, new Vector3(lo[0], lo[1], lo[2]), new Vector3(hi[0], hi[1], hi[2])));
        }
        foreach (var (box, label) in work.Centers)
            if (label == cell)
                result.Add((work.CenterAt(box.MinX, box.MinY, box.MinZ), context.Grid.Corner(box.MinX, box.MinY, box.MinZ), context.Grid.Corner(box.MaxX, box.MaxY, box.MaxZ)));
        return result;
    }

    /**
     * True when every segment from a box to a portal crosses newly solid voxels: the two lie on opposite
     * sides of the closed box on one axis, and all crossings of the near face land on closed voxels.
     */
    private static bool Hidden(EditContext context, TileWork work, Vector3 aMin, Vector3 aMax, Vector3 pMin, Vector3 pMax)
    {
        var grid = context.Grid;
        for (int axis = 0; axis < 3; axis++)
            foreach (bool aBelow in new[] { true, false })
            {
                float near = work.Plane(axis, low: aBelow), far = work.Plane(axis, low: !aBelow);
                float aEdge = aBelow ? TomeKdTree.Get(aMax, axis) : TomeKdTree.Get(aMin, axis);
                float pEdge = aBelow ? TomeKdTree.Get(pMin, axis) : TomeKdTree.Get(pMax, axis);
                if (aBelow ? aEdge > near || pEdge < far : aEdge < near || pEdge > far) continue;
                // Where segments from the box to the portal can cross the near face: t over every pairing of ends.
                float tMin = float.MaxValue, tMax = float.MinValue;
                foreach (float a in new[] { TomeKdTree.Get(aMin, axis), TomeKdTree.Get(aMax, axis) })
                    foreach (float q in new[] { TomeKdTree.Get(pMin, axis), TomeKdTree.Get(pMax, axis) })
                    {
                        float t = (near - a) / (q - a);
                        if (!float.IsFinite(t)) { tMin = 0; tMax = 1; continue; }
                        tMin = Math.Min(tMin, t); tMax = Math.Max(tMax, t);
                    }
                var lo = new float[3]; var hi = new float[3];
                bool inside = true;
                for (int b = 0; b < 3 && inside; b++)
                {
                    if (b == axis) continue;
                    float l = float.MaxValue, h = float.MinValue;
                    foreach (float t in new[] { Math.Clamp(tMin, 0, 1), Math.Clamp(tMax, 0, 1) })
                    {
                        l = Math.Min(l, (1 - t) * TomeKdTree.Get(aMin, b) + t * TomeKdTree.Get(pMin, b));
                        h = Math.Max(h, (1 - t) * TomeKdTree.Get(aMax, b) + t * TomeKdTree.Get(pMax, b));
                    }
                    lo[b] = l; hi[b] = h;
                    inside = l >= work.Plane(b, low: true) && h <= work.Plane(b, low: false);
                }
                if (!inside) continue;
                int layer = aBelow ? work.ClosedBox.Min(axis) : work.ClosedBox.Max(axis) - 1;
                int ua = (axis + 1) % 3, va = (axis + 2) % 3;
                float Voxel(float w, int a) => (w - TomeKdTree.Get(grid.Origin, a)) / grid.Size;
                int u0 = (int)MathF.Floor(Voxel(lo[ua], ua)), u1 = (int)MathF.Ceiling(Voxel(hi[ua], ua));
                int v0 = (int)MathF.Floor(Voxel(lo[va], va)), v1 = (int)MathF.Ceiling(Voxel(hi[va], va));
                bool solid = true;
                var p = new int[3];
                for (int v = v0; v < Math.Max(v1, v0 + 1) && solid; v++)
                    for (int u = u0; u < Math.Max(u1, u0 + 1) && solid; u++)
                    {
                        p[axis] = layer; p[ua] = u; p[va] = v;
                        solid = work.Closed(p[0], p[1], p[2]);
                    }
                if (solid) return true;
            }
        return false;
    }

    /** A gate portal stays with the piece holding its gate's centre, or the piece past the nearest face of the new solid. */
    private static int GateOwner(EditContext context, TileWork work, int cell, TomePortal portal)
    {
        if (portal.GeometryOffset + 1 >= context.Tome.GateVertices.Count) return cell;
        var (min, max) = GateBox(context, portal);
        var center = (min + max) * 0.5f;
        int owner = Owner(context, work, cell, center);
        if (owner >= 0) return owner;
        var (axis, plane) = Enumerable.Range(0, 3).SelectMany(a => new[] { (a, work.Plane(a, low: true) - context.Grid.Size * 0.5f),
            (a, work.Plane(a, low: false) + context.Grid.Size * 0.5f) }).MinBy(face => MathF.Abs(TomeKdTree.Get(center, face.a) - face.Item2));
        owner = Owner(context, work, cell, TomeKdTree.Set(center, axis, plane));
        return owner < 0 ? cell : owner;
    }

    /** A tile gate portal's geometry is its gate's box, two corners in the gate vertices. */
    private static (Vector3 Min, Vector3 Max) GateBox(EditContext context, TomePortal portal)
    {
        var vertices = context.Tome.GateVertices;
        if (portal.GeometryOffset + 1 >= vertices.Count) return (new(float.MinValue), new(float.MaxValue));
        Vector3 a = vertices[portal.GeometryOffset], b = vertices[portal.GeometryOffset + 1];
        return (Vector3.Min(a, b), Vector3.Max(a, b));
    }

    /** Points every portal that enters a split cell at the piece it now enters. */
    private static int Retarget(EditContext context)
    {
        int dropped = 0;
        var tome = context.Tome;
        for (int slot = 0; slot < tome.Tiles.Count; slot++)
        {
            if (tome.Tiles[slot] is not { } tile) continue;
            foreach (var cell in tile.Cells)
            {
                if (!cell.Portals.Any(p => Splits(context, p))) continue;
                var result = new List<TomePortal>(cell.Portals.Count);
                foreach (var portal in cell.Portals)
                {
                    if (!Splits(context, portal)) { result.Add(portal); continue; }
                    var target = context.Works[(int)portal.Target];
                    if (portal.IsGate)
                    {
                        result.Add(portal with { IndexZ = (uint)GateOwner(context, target, portal.TargetCell, portal) << 16 | portal.Z });
                        continue;
                    }
                    foreach (var piece in Pieces(target, tile, portal))
                    {
                        int owner = Owner(context, target, portal.TargetCell, Side(tile, piece, source: false));
                        if (owner < 0) { dropped++; continue; }
                        result.Add(piece with { IndexZ = (uint)owner << 16 | piece.Z });
                    }
                }
                cell.Portals.Clear();
                cell.Portals.AddRange(result);
            }
        }
        return dropped;
    }

    private static bool Splits(EditContext context, TomePortal portal) =>
        !portal.IsOutside && context.Works.TryGetValue((int)portal.Target, out var work) && work.Split.Contains(portal.TargetCell)
        && context.Tome.Tiles[(int)portal.Target]!.IsLeaf;

    /** The piece of a split cell holding a point: a slab outside the closed box, a centre box inside it, or none. */
    private static int Owner(EditContext context, TileWork work, int cell, Vector3 point)
    {
        if (work.ClosedBox.IsEmpty) return cell;
        if ((work.SlabIndex(point, 0), work.SlabIndex(point, 1), work.SlabIndex(point, 2)) != (1, 1, 1)) return work.SlabCell(cell, point);
        // Inside the box by the planes, so the voxel lookup is clamped into the box.
        var b = work.ClosedBox;
        var v = context.Grid.Box(point, point);
        return work.CenterAt(Math.Clamp(v.MinX, b.MinX, b.MaxX - 1), Math.Clamp(v.MinY, b.MinY, b.MaxY - 1), Math.Clamp(v.MinZ, b.MinZ, b.MaxZ - 1));
    }

    /** A point just behind a portal piece (its source side) or just past it (its target side). */
    private static Vector3 Side(TomeTile tile, TomePortal piece, bool source)
    {
        var (min, max) = PortalBox(tile, piece);
        var center = (min + max) * 0.5f;
        int axis = piece.Axis;
        float direction = (piece.Face & 1) == 1 ? 1 : -1;
        return TomeKdTree.Set(center, axis, TomeKdTree.Get(center, axis) + direction * (source ? -Nudge : Nudge));
    }

    /** Splits a portal rectangle at the closed box's planes that cross it. */
    private static List<TomePortal> Pieces(TileWork work, TomeTile tile, TomePortal portal)
    {
        int axis = portal.Axis, xa = (1 << axis) & 3, ya = (1 << xa) & 3;
        var us = Cuts(work, tile, xa, portal.MinX, portal.MaxX);
        var vs = Cuts(work, tile, ya, portal.MinY, portal.MaxY);
        var pieces = new List<TomePortal>();
        for (int i = 0; i + 1 < us.Count; i++)
            for (int j = 0; j + 1 < vs.Count; j++)
                pieces.Add(portal with { XRange = (uint)us[i] << 16 | (uint)us[i + 1], YRange = (uint)vs[j] << 16 | (uint)vs[j + 1] });
        return pieces;
    }

    private static List<int> Cuts(TileWork work, TomeTile tile, int axis, int min, int max)
    {
        var cuts = new List<int> { min };
        if (!work.ClosedBox.IsEmpty)
            foreach (bool low in new[] { true, false })
            {
                float grid = (work.Plane(axis, low) - TomeKdTree.Get(tile.Min, axis)) / TomeKdTree.Get(tile.Max - tile.Min, axis) * 65535f;
                int at = (int)MathF.Round(grid);
                if (at > min && at < max && !cuts.Contains(at)) cuts.Add(at);
            }
        cuts.Add(max);
        cuts.Sort();
        return cuts;
    }

    /** Portals between neighbouring slabs of each split cell, across the closed box's planes but outside the box. */
    private static int SlabPortals(EditContext context, TileWork work)
    {
        int added = 0;
        foreach (var ((cell, ix, iy, iz), low) in work.Slabs.ToList())
            for (int axis = 0; axis < 3; axis++)
            {
                var index = new[] { ix, iy, iz };
                if (index[axis] == 2) continue;
                var next = (int[])index.Clone();
                next[axis]++;
                if (next.SequenceEqual(new[] { 1, 1, 1 }) || index.SequenceEqual(new[] { 1, 1, 1 })) continue;
                if (!work.Slabs.TryGetValue((cell, next[0], next[1], next[2]), out int high)) continue;
                var (cmin, cmax) = work.SplitBounds[cell];
                var lo = new float[3]; var hi = new float[3];
                for (int a = 0; a < 3; a++)
                {
                    if (a == axis) { lo[a] = hi[a] = work.Plane(a, low: index[a] == 0); continue; }
                    lo[a] = index[a] == 0 ? TomeKdTree.Get(cmin, a) : work.Plane(a, low: index[a] == 1);
                    hi[a] = index[a] == 2 ? TomeKdTree.Get(cmax, a) : work.Plane(a, low: index[a] == 0);
                    lo[a] = Math.Max(lo[a], TomeKdTree.Get(cmin, a));
                    hi[a] = Math.Min(hi[a], TomeKdTree.Get(cmax, a));
                }
                if (Enumerable.Range(0, 3).Any(a => a != axis && lo[a] >= hi[a])) continue;
                var min = new Vector3(lo[0], lo[1], lo[2]);
                var max = new Vector3(hi[0], hi[1], hi[2]);
                work.Tile.Cells[low].Portals.Add(Make(work.Tile, axis * 2 + 1, (uint)work.Slot, high, min, max, hierarchy: false));
                work.Tile.Cells[high].Portals.Add(Make(work.Tile, axis * 2, (uint)work.Slot, low, min, max, hierarchy: false));
                added += 2;
            }
        return added;
    }

    /**
     * Connects each centre box to whatever lies across its faces, only where the original tome let a
     * ray through: the same cell, an opened voxel, or an original portal covering that face.
     */
    private static int CenterPortals(EditContext context, TileWork work)
    {
        int added = 0;
        for (int i = 0; i < work.Centers.Count; i++)
        {
            var (box, label) = work.Centers[i];
            int self = work.CenterAt(box.MinX, box.MinY, box.MinZ);
            for (int axis = 0; axis < 3; axis++)
                for (int side = 0; side < 2; side++)
                {
                    int plane = side == 1 ? box.Max(axis) : box.Min(axis), layer = side == 1 ? plane : plane - 1, inner = side == 1 ? plane - 1 : plane;
                    int ua = (axis + 1) % 3, va = (axis + 2) % 3;
                    int u0 = box.Min(ua), u1 = box.Max(ua), v0 = box.Min(va), v1 = box.Max(va), w = u1 - u0, h = v1 - v0;
                    var forward = new (int Slot, int Cell, int Original)[w * h];
                    var backward = new (int Slot, int Cell, int Original)[w * h];
                    for (int v = v0; v < v1; v++)
                        for (int u = u0; u < u1; u++)
                        {
                            var a = new int[3]; var b = new int[3];
                            a[axis] = inner; b[axis] = layer; a[ua] = b[ua] = u; a[va] = b[va] = v;
                            var (slot, cell, original, isCenter) = Across(context, work, b[0], b[1], b[2]);
                            int at = (v - v0) * w + (u - u0);
                            forward[at] = backward[at] = (-1, Solid, Solid);
                            if (cell < 0 || slot < 0 || slot == work.Slot && cell == self) continue;
                            var point = context.Grid.Corner(b[0], b[1], b[2]) + Half(axis, side, context.Grid.Size);
                            if (Passable(context, work.Slot, a, slot, b, axis * 2 + side, point)) forward[at] = (slot, cell, original);
                            if (!isCenter && Passable(context, slot, b, work.Slot, a, axis * 2 + 1 - side, point)) backward[at] = (slot, cell, original);
                        }
                    added += Emit(context, work, self, label, axis, side, plane, u0, v0, w, h, forward, toNeighbour: true);
                    added += Emit(context, work, self, label, axis, side, plane, u0, v0, w, h, backward, toNeighbour: false);
                }
        }
        return added;
    }

    /** The centre of a voxel face, offset from the voxel corner. */
    private static Vector3 Half(int axis, int side, float size)
    {
        var half = new Vector3(size * 0.5f);
        return TomeKdTree.Set(half, axis, side == 1 ? 0 : size);
    }

    private static int Emit(EditContext context, TileWork work, int self, int label, int axis, int side, int plane, int u0, int v0, int w, int h,
        (int Slot, int Cell, int Original)[] keys, bool toNeighbour)
    {
        int added = 0;
        int ua = (axis + 1) % 3, va = (axis + 2) % 3;
        foreach (var (a0, a1, b0, b1, key) in Rectangles(keys, w, h))
        {
            if (key.Cell < 0) continue;
            var lo = new int[3]; var hi = new int[3];
            lo[axis] = hi[axis] = plane;
            lo[ua] = u0 + a0; hi[ua] = u0 + a1; lo[va] = v0 + b0; hi[va] = v0 + b1;
            Vector3 min = context.Grid.Corner(lo[0], lo[1], lo[2]), max = context.Grid.Corner(hi[0], hi[1], hi[2]);
            var selfCell = work.Tile.Cells[self];
            if (toNeighbour)
                added += context.AddPortal(work.Slot, selfCell, key.Slot, key.Cell, key.Original, axis * 2 + side, min, max);
            else
                added += context.AddPortal(key.Slot, context.Tome.Tiles[key.Slot]!.Cells[key.Cell], work.Slot, self, label,
                    axis * 2 + 1 - side, min, max);
        }
        return added;
    }

    /** The cell across a voxel face after the edit, its original cell, and whether it is another centre box. */
    private static (int Slot, int Cell, int Original, bool IsCenter) Across(EditContext context, TileWork work, int x, int y, int z)
    {
        if (work.TileVoxels.Contains(x, y, z)) return (work.Slot, work.NewCellAt(x, y, z), work.Label(x, y, z), work.InCenter(x, y, z));
        int slot = context.SlotAt(x, y, z);
        if (slot < 0) return (-1, Outside, Outside, false);
        if (context.Works.TryGetValue(slot, out var other)) return (slot, other.NewCellAt(x, y, z), other.Label(x, y, z), other.InCenter(x, y, z));
        int cell = context.OriginalCell(slot, x, y, z);
        return (slot, cell, cell, false);
    }


    /** Whether a ray could cross from voxel a in one tile into voxel b in another before the edit, less the new solid. */
    private static bool Passable(EditContext context, int fromSlot, int[] a, int toSlot, int[] b, int face, Vector3 point)
    {
        context.Works.TryGetValue(fromSlot, out var from);
        context.Works.TryGetValue(toSlot, out var to);
        if (from?.Closed(a[0], a[1], a[2]) == true || to?.Closed(b[0], b[1], b[2]) == true) return false;
        bool openA = from?.Opened(a[0], a[1], a[2]) == true, openB = to?.Opened(b[0], b[1], b[2]) == true;
        int cellA = from?.Original(a[0], a[1], a[2]) ?? context.OriginalCell(fromSlot, a[0], a[1], a[2]);
        int cellB = to?.Original(b[0], b[1], b[2]) ?? context.OriginalCell(toSlot, b[0], b[1], b[2]);
        if (openA && (openB || cellB >= 0) || openB && cellA >= 0) return true;
        if (cellA < 0 || cellB < 0) return false;
        if (fromSlot == toSlot && cellA == cellB) return true;
        return context.HadPortal(fromSlot, cellA, toSlot, cellB, face, point);
    }
}
