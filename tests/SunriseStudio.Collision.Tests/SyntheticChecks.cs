using System.Buffers.Binary;
using System.Numerics;
using SunriseStudio.Scene.Collision;
using static Checks;

/** Tree codec, refit and flag checks on built data. */
internal static class SyntheticChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run()
    {
        var parent = new CompoundBox(0, 0, 0, 226, 226, 226);
        var child = CompoundTree.Decode(parent, [0x12, 0x00, 0xFF]);
        Check(child == new CompoundBox(1, 0, 225, 222, 226, 1), "Tree node codes decode against the wrong steps.");

        Check(StaticCompoundShape.Flags(QsTransform.Identity, false) == 0, "Identity has flags.");
        Check(StaticCompoundShape.Flags(QsTransform.Identity, true) == 1, "The convex flag was lost.");
        Check(StaticCompoundShape.Flags(QsTransform.Identity with { Translation = Vector3.UnitX }, false) == 2, "A move is not flag 2.");
        Check(StaticCompoundShape.Flags(QsTransform.Identity with { Scale = new(2) }, false) == 6, "A scale is not flags 2 and 4.");
        Check(StaticCompoundShape.Flags(QsTransform.Identity with { Scale = new(-1) }, true) == 15, "A mirror is not flag 8.");
        Check(StaticCompoundShape.Flags(QsTransform.Identity with { Scale = new(-1, -1, 1) }, false) == 6, "Two negative lanes are not a mirror.");

        var random = new Random(7);
        int trees = 0;
        foreach (int leafCount in new[] { 1, 2, 3, 17, 500 })
        {
            var ids = Enumerable.Range(0, leafCount).OrderBy(_ => random.Next()).ToArray();
            byte[] nodes = Build(ids, 0, leafCount);
            var leaves = Enumerable.Range(0, leafCount).Select(_ => RandomBox(random, 100)).ToArray();
            var (refit, min, max) = CompoundTree.Refit(nodes, leafCount, leaves);
            Check(refit.Length == nodes.Length, "Refit changed the node count.");
            for (int i = 0; i < nodes.Length; i += CompoundTree.NodeSize)
                Check(nodes.AsSpan(i + 3, 3).SequenceEqual(refit.AsSpan(i + 3, 3)), "Refit changed the topology.");
            Encloses(refit, leafCount, min, max, leaves);
            Check(CompoundTree.Refit(nodes, leafCount, leaves).Nodes.SequenceEqual(refit), "Refit is not deterministic.");

            // One leaf moves far outside the old domain; every ancestor must grow to hold it.
            leaves[ids[^1]] = RandomBox(random, 5000) with { MinX = 4000, MaxX = 4003 };
            var (moved, movedMin, movedMax) = CompoundTree.Refit(nodes, leafCount, leaves);
            Encloses(moved, leafCount, movedMin, movedMax, leaves);
            Check(movedMax.X >= 4003, "The domain did not grow to the moved leaf.");
            trees++;
        }
        byte[] repeated = Build([0, 0], 0, 2);
        Throws<InvalidDataException>(() => CompoundTree.LeafBoxes(repeated, 2, new CompoundBox(0, 0, 0, 1, 1, 1)));
        Console.WriteLine($"PASS collision-synthetic codec=decoded flags=1,2,4,8 refit-trees={trees} topology=kept "
            + "enclosure=all-leaves moved-leaf=enclosed repeated-leaf=refused");
    }

    /** Checks each decoded leaf against its target box with no tolerance. */
    private static void Encloses(byte[] nodes, int count, Vector3 min, Vector3 max, CompoundBox[] targets)
    {
        var decoded = CompoundTree.LeafBoxes(nodes, count, CompoundBox.From(min, max));
        for (int i = 0; i < count; i++) Check(decoded[i].Contains(targets[i]), $"Leaf {i} does not enclose its box.");
    }

    /** Pre-order: a left subtree of k leaves has 2k - 1 nodes, so the right child is at i + 2k. */
    private static byte[] Build(int[] ids, int first, int count)
    {
        if (count == 1) return Node(false, ids[first]);
        int left = count / 2;
        return [.. Node(true, left), .. Build(ids, first, left), .. Build(ids, first + left, count - left)];
    }

    private static byte[] Node(bool isInternal, int value)
    {
        var node = new byte[CompoundTree.NodeSize];
        node[3] = (byte)((isInternal ? 0x80 : 0) | (value >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(4), (ushort)value);
        return node;
    }

    private static CompoundBox RandomBox(Random random, double range)
    {
        double x = random.NextDouble() * range, y = random.NextDouble() * range, z = -random.NextDouble() * range;
        return new(x, y, z, x + random.NextDouble() * 5, y + random.NextDouble() * 5, z + random.NextDouble() * 5);
    }
}
