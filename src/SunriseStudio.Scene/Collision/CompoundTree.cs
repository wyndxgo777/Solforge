using System.Buffers.Binary;
using System.Numerics;

namespace SunriseStudio.Scene.Collision;

/** A box in double precision, so the tree codec can stay conservative in float. */
public readonly record struct CompoundBox(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    public Vector3 Min => new((float)MinX, (float)MinY, (float)MinZ);
    public Vector3 Max => new((float)MaxX, (float)MaxY, (float)MaxZ);
    /** Box with the given corners. */
    public static CompoundBox From(Vector3 min, Vector3 max) => new(min.X, min.Y, min.Z, max.X, max.Y, max.Z);
    /** Smallest box containing both boxes. */
    public CompoundBox Union(CompoundBox other) => new(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY),
        Math.Min(MinZ, other.MinZ), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY), Math.Max(MaxZ, other.MaxZ));
    /** True when inner lies inside this box, within tolerance. */
    public bool Contains(CompoundBox inner, double tolerance = 0) => inner.MinX >= MinX - tolerance
        && inner.MinY >= MinY - tolerance && inner.MinZ >= MinZ - tolerance && inner.MaxX <= MaxX + tolerance
        && inner.MaxY <= MaxY + tolerance && inner.MaxZ <= MaxZ + tolerance;
}

/**
 * The static compound tree. A 6-byte node is three axis codes, a hi byte and a u16. Hi bit 7 marks
 * an internal node, children i + 1 and i + 2v. A leaf holds instance v = (hi & 0x7F) << 16 | u16.
 */
public static class CompoundTree
{
    public const int NodeSize = 6;
    // A node value has 23 bits: 7 in the hi byte and 16 in the u16.
    public const int MaxInstances = 1 << 23;
    // One code step is 1/226 of the parent extent; 15 squared is the largest step count.
    private const double Steps = 226.0;

    /**
     * Builds a new tree over every instance by median splits on the longest axis of the leaf centers.
     * @param leaves The box each instance's leaf must enclose, indexed by instance.
     * @return 2n - 1 nodes and the domain, which is the root box.
     */
    public static (byte[] Nodes, Vector3 DomainMin, Vector3 DomainMax) Build(IReadOnlyList<CompoundBox> leaves)
    {
        if (leaves.Count is 0 or > MaxInstances) throw new ArgumentException("A compound tree needs 1 to 2^23 leaves.", nameof(leaves));
        var nodes = new byte[(2 * leaves.Count - 1) * NodeSize];
        var centers = leaves.Select(box => new[] { box.MinX + box.MaxX, box.MinY + box.MaxY, box.MinZ + box.MaxZ }).ToArray();
        Split(Enumerable.Range(0, leaves.Count).ToArray(), 0);
        return Refit(nodes, leaves.Count, leaves);

        int Split(int[] items, int node)
        {
            if (items.Length == 1)
            {
                WriteValue(nodes, node, items[0], false);
                return node + 1;
            }
            int axis = 0;
            double extent = -1;
            for (int a = 0; a < 3; a++)
            {
                double low = double.MaxValue, high = double.MinValue;
                foreach (int item in items) { low = Math.Min(low, centers[item][a]); high = Math.Max(high, centers[item][a]); }
                if (high - low > extent) (axis, extent) = (a, high - low);
            }
            var ordered = items.OrderBy(item => centers[item][axis]).ThenBy(item => item).ToArray();
            int half = ordered.Length / 2;
            int right = Split(ordered[..half], node + 1);
            // A left subtree of k leaves has 2k - 1 nodes, so the right child is node + 2k.
            WriteValue(nodes, node, (right - node) / 2, true);
            return Split(ordered[half..], right);
        }
    }

    private static void WriteValue(byte[] nodes, int node, int value, bool internalNode)
    {
        int at = node * NodeSize;
        nodes[at + 3] = (byte)((internalNode ? 0x80 : 0) | (value >> 16 & 0x7F));
        BinaryPrimitives.WriteUInt16LittleEndian(nodes.AsSpan(at + 4), (ushort)value);
    }

    /** A child box per axis: parent min + (c >> 4)^2 * s, parent max - (c & 15)^2 * s. */
    public static CompoundBox Decode(CompoundBox parent, ReadOnlySpan<byte> codes)
    {
        var (minX, maxX) = Axis(parent.MinX, parent.MaxX, codes[0]);
        var (minY, maxY) = Axis(parent.MinY, parent.MaxY, codes[1]);
        var (minZ, maxZ) = Axis(parent.MinZ, parent.MaxZ, codes[2]);
        return new(minX, minY, minZ, maxX, maxY, maxZ);

        static (double, double) Axis(double min, double max, byte code)
        {
            double s = (max - min) / Steps;
            return (min + (code >> 4) * (code >> 4) * s, max - (code & 15) * (code & 15) * s);
        }
    }

    /**
     * Decodes every leaf. The root box is the domain moved by the root's own codes.
     * @return Boxes by instance. A tree that misses or repeats a node or an instance is refused.
     */
    public static CompoundBox[] LeafBoxes(byte[] nodes, int instances, CompoundBox domain)
    {
        var leaves = new CompoundBox?[instances];
        foreach (var (node, _, _, box) in Walk(nodes, instances, domain))
        {
            if (IsInternal(nodes, node)) continue;
            int instance = Value(nodes, node);
            if (leaves[instance] is not null) throw new InvalidDataException("Compound tree reaches an instance twice.");
            leaves[instance] = box;
        }
        return leaves.Select(box => box ?? throw new InvalidDataException("Compound tree misses an instance.")).ToArray();
    }

    /**
     * Refits the tree to new leaf boxes and keeps its topology, so node count and file size stay.
     * Every decoded node encloses its subtree's leaf boxes with a slack that grows by level.
     * @param nodes The installed node bytes; they are not changed.
     * @param leaves The box each instance's leaf must enclose, indexed by instance.
     * @return New node bytes and the new domain, which is the root box.
     */
    public static (byte[] Nodes, Vector3 DomainMin, Vector3 DomainMax) Refit(byte[] nodes, int instances,
        IReadOnlyList<CompoundBox> leaves)
    {
        if (leaves.Count != instances) throw new ArgumentException("One leaf box is needed per instance.", nameof(leaves));
        byte[] result = nodes.ToArray();
        int count = nodes.Length / NodeSize;
        var order = Walk(nodes, instances, default).ToArray();
        var subtree = new CompoundBox[count];
        // Pre-order lists a parent before its children, so a reverse pass builds unions bottom up.
        for (int i = order.Length - 1; i >= 0; i--)
        {
            int node = order[i].Node;
            subtree[node] = IsInternal(nodes, node)
                ? subtree[node + 1].Union(subtree[node + 2 * Value(nodes, node)]) : leaves[Value(nodes, node)];
        }
        var root = subtree[0];
        double magnitude = Math.Max(1, new[] { root.MinX, root.MinY, root.MinZ, root.MaxX, root.MaxY, root.MaxZ }.Max(Math.Abs));
        // The client decodes in float; it can differ from this double decode by ulps per level.
        double step = 4 * magnitude * Math.Pow(2, -23);
        Vector3 domainMin = new(Down(root.MinX - step), Down(root.MinY - step), Down(root.MinZ - step));
        Vector3 domainMax = new(Up(root.MaxX + step), Up(root.MaxY + step), Up(root.MaxZ + step));
        var decoded = new CompoundBox[count];
        decoded[0] = CompoundBox.From(domainMin, domainMax);
        result[0] = result[1] = result[2] = 0;
        foreach (var (node, parent, depth, _) in order)
        {
            if (node == 0) continue;
            double slack = step * (depth + 1);
            var target = subtree[node];
            var outer = decoded[parent];
            int at = node * NodeSize;
            result[at] = EncodeAxis(outer.MinX, outer.MaxX, target.MinX - slack, target.MaxX + slack);
            result[at + 1] = EncodeAxis(outer.MinY, outer.MaxY, target.MinY - slack, target.MaxY + slack);
            result[at + 2] = EncodeAxis(outer.MinZ, outer.MaxZ, target.MinZ - slack, target.MaxZ + slack);
            decoded[node] = Decode(outer, result.AsSpan(at, 3));
            if (!decoded[node].Contains(target)) throw new InvalidDataException("Compound tree node cannot enclose its children.");
        }
        return (result, domainMin, domainMax);
    }

    /** The largest codes whose decoded interval still holds [low, high]. */
    internal static byte EncodeAxis(double min, double max, double low, double high)
    {
        double s = (max - min) / Steps;
        int a = 0, b = 0;
        for (int k = 15; k > 0 && s > 0; k--) if (min + k * k * s <= low) { a = k; break; }
        for (int k = 15; k > 0 && s > 0; k--) if (max - k * k * s >= high) { b = k; break; }
        return (byte)(a << 4 | b);
    }

    private static bool IsInternal(byte[] nodes, int node) => (nodes[node * NodeSize + 3] & 0x80) != 0;

    private static int Value(byte[] nodes, int node) =>
        (nodes[node * NodeSize + 3] & 0x7F) << 16 | BinaryPrimitives.ReadUInt16LittleEndian(nodes.AsSpan(node * NodeSize + 4));

    /** Pre-order walk with parent, depth and decoded box. Every node must be reached once. */
    private static IEnumerable<(int Node, int Parent, int Depth, CompoundBox Box)> Walk(byte[] nodes, int instances, CompoundBox domain)
    {
        int count = nodes.Length / NodeSize;
        if (count == 0) yield break;
        if (nodes.Length % NodeSize != 0) throw new InvalidDataException("Compound tree node bytes are truncated.");
        var seen = new bool[count];
        var stack = new Stack<(int Node, int Parent, int Depth, CompoundBox Box)>();
        stack.Push((0, -1, 0, Decode(domain, nodes.AsSpan(0, 3))));
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            int node = item.Node;
            if (seen[node]) throw new InvalidDataException("Compound tree node is shared.");
            seen[node] = true;
            yield return item;
            if (IsInternal(nodes, node))
            {
                int right = node + 2 * Value(nodes, node);
                if (right <= node + 1 || right >= count) throw new InvalidDataException("Compound tree child is out of range.");
                // Push right first so the left subtree is walked first.
                stack.Push((right, node, item.Depth + 1, Decode(item.Box, nodes.AsSpan(right * NodeSize, 3))));
                stack.Push((node + 1, node, item.Depth + 1, Decode(item.Box, nodes.AsSpan((node + 1) * NodeSize, 3))));
            }
            else if (Value(nodes, node) >= instances) throw new InvalidDataException("Compound tree leaf names a missing instance.");
        }
        if (seen.Contains(false)) throw new InvalidDataException("Compound tree has unreachable nodes.");
    }

    private static float Down(double value) => (float)value <= value ? (float)value : MathF.BitDecrement((float)value);
    private static float Up(double value) => (float)value >= value ? (float)value : MathF.BitIncrement((float)value);
}
