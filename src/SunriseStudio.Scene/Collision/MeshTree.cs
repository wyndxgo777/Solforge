using System.Buffers.Binary;

namespace SunriseStudio.Scene.Collision;

/** The top tree over a mesh's sections, or a section's tree over its primitives. */
public enum MeshTreeKind
{
    /** 5-byte nodes: codes, hi, lo. Hi bit 7 marks internal; right child i + 2 * 15-bit value. */
    Top,
    /** 4-byte nodes: codes, data. Data bit 0 marks internal; right child i + (data & 0xFE). */
    Section
}

/** One node of a mesh tree walk. Right is -1 on a leaf; Leaf is -1 on an internal node. */
public readonly record struct MeshTreeNode(int Index, int Parent, int Depth, CompoundBox Box, int Right, int Leaf);

/**
 * Walks, builds and refits compressed mesh trees. The root box is the tree's domain; the root codes
 * are not read. A child box is coded against its parent the same way as in the compound tree.
 */
public static class MeshTree
{
    public const int TopNodeSize = 5, SectionNodeSize = 4;
    // A top leaf holds a 15-bit section; a section leaf holds a 7-bit primitive.
    public const int MaxSections = 0x7FFF, MaxPrimitives = 128;

    /** Size in bytes of one node of the given tree kind. */
    public static int NodeSize(MeshTreeKind kind) => kind == MeshTreeKind.Top ? TopNodeSize : SectionNodeSize;

    /**
     * Pre-order walk. Every node must be reached once and every leaf value be below leafCount.
     * @return The nodes in pre-order; a parent comes before its children.
     */
    public static MeshTreeNode[] Walk(MeshTreeKind kind, byte[] nodes, CompoundBox root, int leafCount)
    {
        int size = NodeSize(kind);
        if (nodes.Length % size != 0) throw new InvalidDataException("Mesh tree node bytes are truncated.");
        int count = nodes.Length / size;
        var result = new List<MeshTreeNode>(count);
        if (count == 0) return [];
        var seen = new bool[count];
        var stack = new Stack<MeshTreeNode>();
        stack.Push(Node(kind, nodes, 0, -1, 0, root));
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (seen[node.Index]) throw new InvalidDataException("Mesh tree node is reached twice.");
            seen[node.Index] = true;
            result.Add(node);
            if (node.Right >= 0)
            {
                if (node.Right <= node.Index + 1 || node.Right >= count) throw new InvalidDataException("Mesh tree child is out of range.");
                stack.Push(Node(kind, nodes, node.Right, node.Index, node.Depth + 1,
                    CompoundTree.Decode(node.Box, nodes.AsSpan(node.Right * size, 3))));
                stack.Push(Node(kind, nodes, node.Index + 1, node.Index, node.Depth + 1,
                    CompoundTree.Decode(node.Box, nodes.AsSpan((node.Index + 1) * size, 3))));
            }
            else if (node.Leaf >= leafCount) throw new InvalidDataException("Mesh tree leaf names a missing entry.");
        }
        if (seen.Contains(false)) throw new InvalidDataException("Mesh tree has unreachable nodes.");
        return result.ToArray();
    }

    /** The box of one section-tree node, found by the client's descent from the root. */
    public static CompoundBox SectionNodeBox(byte[] nodes, CompoundBox root, int target)
    {
        var box = root;
        int node = 0;
        while (node != target)
        {
            if (node * SectionNodeSize >= nodes.Length || (nodes[node * SectionNodeSize + 3] & 1) == 0)
                throw new InvalidDataException("Section tree node is not reachable by descent.");
            int right = node + (nodes[node * SectionNodeSize + 3] & 0xFE);
            node = target >= right ? right : node + 1;
            box = CompoundTree.Decode(box, nodes.AsSpan(node * SectionNodeSize, 3));
        }
        return box;
    }

    /**
     * Builds a tree over leaf boxes by median splits on the longest axis of the box centers.
     * @param order Receives the input index of each leaf in pre-order; leaf k holds value k.
     * @return Node bytes with codes that enclose every leaf box inside root.
     */
    public static byte[] Build(MeshTreeKind kind, IReadOnlyList<CompoundBox> leaves, CompoundBox root, out int[] order)
    {
        if (leaves.Count == 0) throw new ArgumentException("A mesh tree needs at least one leaf.", nameof(leaves));
        if (leaves.Count > (kind == MeshTreeKind.Top ? MaxSections : MaxPrimitives))
            throw new ArgumentException("Too many leaves for this mesh tree.", nameof(leaves));
        int size = NodeSize(kind);
        var bytes = new byte[(2 * leaves.Count - 1) * size];
        var leafOrder = new List<int>(leaves.Count);
        Split(Enumerable.Range(0, leaves.Count).ToArray(), 0);
        order = leafOrder.ToArray();
        var sorted = order.Select(i => leaves[i]).ToArray();
        return Refit(kind, bytes, root, sorted);

        int Split(int[] items, int node)
        {
            if (items.Length == 1)
            {
                WriteLeaf(kind, bytes, node, leafOrder.Count);
                leafOrder.Add(items[0]);
                return node + 1;
            }
            var centers = items.Select(i => Center(leaves[i])).ToArray();
            int axis = LongestAxis(centers);
            var ordered = items.Zip(centers).OrderBy(x => x.Second[axis]).ThenBy(x => x.First).Select(x => x.First).ToArray();
            int half = ordered.Length / 2;
            int left = Split(ordered[..half], node + 1);
            WriteInternal(kind, bytes, node, left - node);
            return Split(ordered[half..], left);
        }
    }

    /**
     * Rewrites every node's codes for new leaf boxes and keeps the topology and the byte count.
     * @param root The fixed root box; it must enclose every leaf box.
     * @param leaves The box each leaf value must enclose, indexed by leaf value.
     */
    public static byte[] Refit(MeshTreeKind kind, byte[] nodes, CompoundBox root, IReadOnlyList<CompoundBox> leaves)
    {
        int size = NodeSize(kind);
        var walk = Walk(kind, nodes, default, leaves.Count);
        var subtree = new CompoundBox[nodes.Length / size];
        for (int i = walk.Length - 1; i >= 0; i--)
        {
            var node = walk[i];
            subtree[node.Index] = node.Right >= 0 ? subtree[node.Index + 1].Union(subtree[node.Right]) : leaves[node.Leaf];
        }
        if (!root.Contains(subtree[0])) throw new InvalidDataException("Mesh tree root does not enclose its leaves.");
        double magnitude = Math.Max(1, new[] { root.MinX, root.MinY, root.MinZ, root.MaxX, root.MaxY, root.MaxZ }.Max(Math.Abs));
        // The client decodes in float; it can differ from this double decode by ulps per level.
        double step = 4 * magnitude * Math.Pow(2, -23);
        byte[] result = nodes.ToArray();
        var decoded = new CompoundBox[subtree.Length];
        decoded[0] = root;
        result[0] = result[1] = result[2] = 0;
        foreach (var node in walk)
        {
            if (node.Index == 0) continue;
            double slack = step * (node.Depth + 1);
            var target = subtree[node.Index];
            var outer = decoded[node.Parent];
            int at = node.Index * size;
            // Slack never pushes a target past the parent box it must fit in.
            result[at] = CompoundTree.EncodeAxis(outer.MinX, outer.MaxX, Math.Max(outer.MinX, target.MinX - slack),
                Math.Min(outer.MaxX, target.MaxX + slack));
            result[at + 1] = CompoundTree.EncodeAxis(outer.MinY, outer.MaxY, Math.Max(outer.MinY, target.MinY - slack),
                Math.Min(outer.MaxY, target.MaxY + slack));
            result[at + 2] = CompoundTree.EncodeAxis(outer.MinZ, outer.MaxZ, Math.Max(outer.MinZ, target.MinZ - slack),
                Math.Min(outer.MaxZ, target.MaxZ + slack));
            decoded[node.Index] = CompoundTree.Decode(outer, result.AsSpan(at, 3));
            if (!decoded[node.Index].Contains(target)) throw new InvalidDataException("Mesh tree node cannot enclose its children.");
        }
        return result;
    }

    /** Reads one node: a right child for an internal node, a leaf value otherwise. */
    private static MeshTreeNode Node(MeshTreeKind kind, byte[] nodes, int index, int parent, int depth, CompoundBox box)
    {
        if (kind == MeshTreeKind.Top)
        {
            int hi = nodes[index * TopNodeSize + 3], lo = nodes[index * TopNodeSize + 4];
            return (hi & 0x80) != 0
                ? new(index, parent, depth, box, index + 2 * ((hi & 0x7F) << 8 | lo), -1)
                : new(index, parent, depth, box, -1, hi << 8 | lo);
        }
        int data = nodes[index * SectionNodeSize + 3];
        return (data & 1) != 0 ? new(index, parent, depth, box, index + (data & 0xFE), -1) : new(index, parent, depth, box, -1, data >> 1);
    }

    private static void WriteLeaf(MeshTreeKind kind, byte[] bytes, int node, int value)
    {
        if (kind == MeshTreeKind.Top) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(node * TopNodeSize + 3), (ushort)value);
        else bytes[node * SectionNodeSize + 3] = (byte)(value << 1);
    }

    private static void WriteInternal(MeshTreeKind kind, byte[] bytes, int node, int offset)
    {
        // A left subtree of k leaves has 2k - 1 nodes, so the right offset 2k is even.
        if (kind == MeshTreeKind.Top)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(node * TopNodeSize + 3), (ushort)(0x8000 | offset / 2));
        else if (offset > 0xFE) throw new InvalidDataException("Section tree right offset does not fit its byte.");
        else bytes[node * SectionNodeSize + 3] = (byte)(offset | 1);
    }

    private static double[] Center(CompoundBox box) =>
        [(box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2, (box.MinZ + box.MaxZ) / 2];

    private static int LongestAxis(double[][] centers)
    {
        int best = 0;
        double extent = -1;
        for (int axis = 0; axis < 3; axis++)
        {
            double e = centers.Max(c => c[axis]) - centers.Min(c => c[axis]);
            if (e > extent) (best, extent) = (axis, e);
        }
        return best;
    }
}
