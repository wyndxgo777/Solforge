using System.Numerics;

namespace SunriseStudio.Formats;

/** Tile node tree used while editing. */
public static partial class TomeEditor
{
    private sealed class Node
    {
        internal byte Code = TomeKdTree.Leaf;
        internal float Split;
        internal Node? Left, Right;
        internal int Entry;
    }

    /** Rebuilds a tile's view tree and cell map; nodes away from the edit keep their exact splits and entries. */
    private static void CommitTree(EditContext context, TileWork work)
    {
        var tile = work.Tile;
        var original = tile.ViewTree!;
        var ranks = original.Ranks();
        var grid = context.Grid;
        Vector3 regionMin = grid.Corner(work.Box.MinX, work.Box.MinY, work.Box.MinZ), regionMax = grid.Corner(work.Box.MaxX, work.Box.MaxY, work.Box.MaxZ);

        Node Rebuild(int index, Vector3 min, Vector3 max)
        {
            int code = original.Codes[index];
            if (code == TomeKdTree.Leaf)
                return Graft(min, max, tile.CellMap.Count == 0 ? TomeTile.NoCell : tile.CellMap[index - ranks[index]]);
            float split = index < original.Splits.Count ? original.Splits[index] : (TomeKdTree.Get(min, code) + TomeKdTree.Get(max, code)) * 0.5f;
            int right = ranks[index] << 1;
            return new Node
            {
                Code = (byte)code, Split = split,
                Left = Rebuild(right - 1, min, TomeKdTree.Set(max, code, split)),
                Right = Rebuild(right, TomeKdTree.Set(min, code, split), max)
            };
        }

        // Parts of a leaf outside the region keep the leaf's cell, or its slab when the cell is split.
        Node Graft(Vector3 min, Vector3 max, int entry)
        {
            if (!Overlap(min, max, regionMin, regionMax)) return Slabs(min, max, entry);
            for (int axis = 0; axis < 3; axis++)
            {
                float low = TomeKdTree.Get(regionMin, axis), high = TomeKdTree.Get(regionMax, axis);
                if (low > TomeKdTree.Get(min, axis) + 1e-4f)
                    return Cut(axis, low, min, max, (a, b) => Slabs(a, b, entry), (a, b) => Graft(a, b, entry));
                if (high < TomeKdTree.Get(max, axis) - 1e-4f)
                    return Cut(axis, high, min, max, (a, b) => Graft(a, b, entry), (a, b) => Slabs(a, b, entry));
            }
            return Inside(min, max, entry);
        }

        // A cut reached through box middles stores no split; the tile stores splits up to the last other one.
        Node Cut(int axis, float plane, Vector3 min, Vector3 max, Func<Vector3, Vector3, Node> below, Func<Vector3, Vector3, Node> above, int depth = 0)
        {
            float middle = (TomeKdTree.Get(min, axis) + TomeKdTree.Get(max, axis)) * 0.5f;
            if (MathF.Abs(middle - plane) < 1e-4f)
                return Inner(axis, middle, below(min, TomeKdTree.Set(max, axis, middle)), above(TomeKdTree.Set(min, axis, middle), max));
            float step = (middle - TomeKdTree.Get(grid.Origin, axis)) / grid.Size;
            if (depth >= 24 || MathF.Abs(step - MathF.Round(step)) > 1e-3f)
                return Inner(axis, plane, below(min, TomeKdTree.Set(max, axis, plane)), above(TomeKdTree.Set(min, axis, plane), max));
            return plane < middle
                ? Inner(axis, middle, Cut(axis, plane, min, TomeKdTree.Set(max, axis, middle), below, above, depth + 1), above(TomeKdTree.Set(min, axis, middle), max))
                : Inner(axis, middle, below(min, TomeKdTree.Set(max, axis, middle)), Cut(axis, plane, TomeKdTree.Set(min, axis, middle), max, below, above, depth + 1));
        }

        Node Slabs(Vector3 min, Vector3 max, int entry)
        {
            if (!Touched(entry)) return new Node { Entry = entry };
            for (int axis = 0; axis < 3; axis++)
                foreach (bool low in new[] { true, false })
                {
                    float plane = work.Plane(axis, low);
                    if (plane > TomeKdTree.Get(min, axis) + 1e-4f && plane < TomeKdTree.Get(max, axis) - 1e-4f)
                        return Cut(axis, plane, min, max, (a, b) => Slabs(a, b, entry), (a, b) => Slabs(a, b, entry));
                }
            var middle = (min + max) * 0.5f;
            // A cell group naming a split cell gets a copy that names this piece's slab instead.
            return new Node { Entry = entry >= 0 ? work.SlabCell(entry, middle) : work.Group(entry, middle) };
        }

        bool Touched(int entry) => entry >= 0 ? work.Split.Contains(entry) : entry < TomeTile.NoCell && tile.CellsOf(entry).Any(work.Split.Contains);

        // Inside the region every voxel takes its edited cell; uniform blocks become one leaf.
        Node Inside(Vector3 wholeMin, Vector3 wholeMax, int entry)
        {
            var whole = grid.Box(wholeMin, wholeMax).Intersect(work.Box);
            var entries = new int[whole.Count];
            for (int z = whole.MinZ; z < whole.MaxZ; z++)
                for (int y = whole.MinY; y < whole.MaxY; y++)
                    for (int x = whole.MinX; x < whole.MaxX; x++) entries[At(x, y, z)] = work.NewEntry(grid.Center(x, y, z), entry);
            return Part(wholeMin, wholeMax);

            int At(int x, int y, int z) => ((z - whole.MinZ) * whole.SizeY + (y - whole.MinY)) * whole.SizeX + (x - whole.MinX);
            int Entry(int axis, int plane, int u, int v) =>
                axis == 0 ? entries[At(plane, u, v)] : axis == 1 ? entries[At(u, plane, v)] : entries[At(u, v, plane)];

            // A split where the entries change, nearest the box middle, keeps the leaves few.
            Node Part(Vector3 min, Vector3 max)
            {
                var box = grid.Box(min, max).Intersect(whole);
                int bestAxis = -1, bestPlane = 0;
                float bestOffset = float.MaxValue;
                for (int axis = 0; axis < 3; axis++)
                {
                    int ua = axis == 0 ? 1 : 0, va = axis == 2 ? 1 : 2;
                    int start = box.Min(axis), size = box.Max(axis) - start;
                    // Planes from the middle outward; the first that changes is this axis's best.
                    foreach (int plane in Enumerable.Range(start + 1, size - 1).OrderBy(p => Math.Abs(2 * (p - start) - size)))
                    {
                        float offset = MathF.Abs(plane - start - size * 0.5f) / size;
                        if (offset >= bestOffset) break;
                        if (!Changes(box, axis, plane, ua, va)) continue;
                        (bestAxis, bestPlane, bestOffset) = (axis, plane, offset);
                        break;
                    }
                }
                if (bestAxis < 0) return new Node { Entry = entries[At(box.MinX, box.MinY, box.MinZ)] };
                return Cut(bestAxis, TomeKdTree.Get(grid.Origin, bestAxis) + bestPlane * grid.Size, min, max, Part, Part);
            }

            bool Changes(TomeVoxelBox box, int axis, int plane, int ua, int va)
            {
                for (int v = box.Min(va); v < box.Max(va); v++)
                    for (int u = box.Min(ua); u < box.Max(ua); u++)
                        if (Entry(axis, plane - 1, u, v) != Entry(axis, plane, u, v)) return true;
                return false;
            }
        }

        var root = Rebuild(0, tile.Min, tile.Max);
        var (tree, map) = Serialize(root, tile.Min, tile.Max);
        tile.ViewTree = tree;
        tile.CellMap.Clear();
        tile.CellMap.AddRange(map);
        tile.MapWidthOverride = null;
        tile.ClusterCountOverride = null;
    }

    private static Node Inner(int axis, float split, Node left, Node right) => new() { Code = (byte)axis, Split = split, Left = left, Right = right };

    /** Breadth-first order; splits are stored up to the last node whose split is not its box middle, leaves storing 0. */
    private static (TomeKdTree Tree, List<int> Map) Serialize(Node root, Vector3 tileMin, Vector3 tileMax)
    {
        var codes = new List<byte>();
        var splits = new List<float>();
        var map = new List<int>();
        int stored = 0;
        var queue = new Queue<(Node Node, Vector3 Min, Vector3 Max)>();
        queue.Enqueue((root, tileMin, tileMax));
        while (queue.Count > 0)
        {
            var (node, min, max) = queue.Dequeue();
            codes.Add(node.Code);
            if (node.Code == TomeKdTree.Leaf) { splits.Add(0f); map.Add(node.Entry); continue; }
            float middle = (TomeKdTree.Get(min, node.Code) + TomeKdTree.Get(max, node.Code)) * 0.5f;
            if (BitConverter.SingleToInt32Bits(middle) != BitConverter.SingleToInt32Bits(node.Split)) stored = codes.Count;
            splits.Add(node.Split);
            queue.Enqueue((node.Left!, min, TomeKdTree.Set(max, node.Code, node.Split)));
            queue.Enqueue((node.Right!, TomeKdTree.Set(min, node.Code, node.Split), max));
        }
        splits.RemoveRange(stored, splits.Count - stored);
        return (new TomeKdTree { Codes = codes, Splits = splits }, map);
    }
}
