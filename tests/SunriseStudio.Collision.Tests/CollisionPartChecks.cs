using System.Numerics;
using SunriseStudio.Authoring;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using static Checks;

/** The sabre tree's five leaf hulls in map mesh 0x80B91745, scaled with the trunk about its origin. */
internal static class CollisionPartChecks
{
    private const uint SabreMap = 0x80FAA003, LeafMesh = 0x80B91745;
    private static readonly Vector3 TreeOrigin = new(-38.86209f, 35.410763f, 2.4619646f);

    /** The tree's five leaf hulls: convex parts inside the tree's box. */
    internal static int[] Leaves(CollisionScene collision, int index)
    {
        var placement = collision.Placements[index];
        var shape = collision.Pieces[placement.Piece].Shape;
        var parts = CollisionParts.Of(shape);
        return Enumerable.Range(0, parts.Triangles.Count).Where(part =>
        {
            var (min, max) = parts.Bounds(shape, [part], placement.World);
            return parts.IsPrimitive(shape, part) && min.X > -46.6f && max.X < -31.3f && min.Y > 28f && max.Y < 43.3f && min.Z > 4f
                && max.Z < 16f;
        }).ToArray();
    }

    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run(PackageCatalog catalog)
    {
        var scene = new AssetSceneLoader(catalog).Load(SabreMap);
        var collision = new CollisionSceneLoader(catalog).Load(scene);
        int index = Enumerable.Range(0, collision.Placements.Count).Single(i => collision.Pieces[collision.Placements[i].Piece].Tag == LeafMesh);
        var placement = collision.Placements[index];
        var shape = collision.Pieces[placement.Piece].Shape;
        var parts = CollisionParts.Of(shape);
        int[] leaves = Leaves(collision, index);
        Check(leaves.Length == 5, $"Expected the tree's 5 leaf hulls, found {leaves.Length}.");
        var chosen = leaves.Select(part => new CollisionPartRef(index, part)).ToArray();
        var identity = CollisionImport.Plan(catalog, collision, CollisionPartEdit.Input(catalog, collision, chosen, Matrix4x4.Identity),
            catalog.ReadOriginal);
        Check(identity.Assets.Count == 0, "An unchanged part edit wrote collision.");
        float factor = 0.05f / 0.413273f;
        var transform = Matrix4x4.CreateTranslation(-TreeOrigin) * Matrix4x4.CreateScale(factor) * Matrix4x4.CreateTranslation(TreeOrigin);
        var input = CollisionPartEdit.Input(catalog, collision, chosen, transform);
        Throws<InvalidDataException>(() => CollisionImport.Plan(catalog, collision, input with { Shapes = [.. input.Shapes, .. input.Shapes] },
            catalog.ReadOriginal));
        var plan = CollisionImport.Plan(catalog, collision, input, catalog.ReadOriginal);
        // Requantized trees carry slack, so the placing compound may get a refit tree as well.
        Check(plan.Assets.ContainsKey(LeafMesh) && plan.Assets.Keys.All(tag => tag == LeafMesh || tag == placement.CompoundTag),
            "Shrinking parts wrote more than the mesh and its compound.");
        byte[] installed = catalog.ReadOriginal(LeafMesh), written = plan.Assets[LeafMesh];
        Check(written.Length == installed.Length, "The mesh changed size.");
        var file = HavokPackfile.Parse(written);
        var after = CollisionShapes.Decode(file, file.RootOffset);
        // Hull triangles are rebuilt from the moved points; the stored vertices and mesh triangles keep their layout.
        int meshIndices = shape.MeshTriangleCount * 3;
        Check(after.Vertices.Length == shape.Vertices.Length && after.MeshTriangleCount == shape.MeshTriangleCount
            && after.Indices.Take(meshIndices).SequenceEqual(shape.Indices.Take(meshIndices))
            && after.TriangleKeys.Distinct().Order().SequenceEqual(shape.TriangleKeys.Distinct().Order()), "The mesh changed its stored layout.");
        var moved = parts.Vertices(shape, leaves);
        var problems = CompressedMesh.Read(file, file.RootOffset).Check();
        Check(problems.Count == 0, "The rewritten mesh fails the client's checks: " + string.Join("; ", problems.Take(3)));
        float movedError = 0, stillError = 0;
        // Shared slots no triangle uses can hold other data, so only used vertices are compared.
        foreach (int vertex in shape.Indices.Distinct())
        {
            var world = Vector3.Transform(after.Vertices[vertex], placement.World);
            var expected = Vector3.Transform(shape.Vertices[vertex], placement.World);
            if (moved.Contains(vertex)) movedError = MathF.Max(movedError, Vector3.Distance(world, Vector3.Transform(expected, transform)));
            else stillError = MathF.Max(stillError, Vector3.Distance(world, expected));
        }
        // Re-packing rounds the rest of the mesh again; it stays within a few centimetres.
        Check(movedError < 0.05f && stillError < 0.05f, $"Part vertices land {movedError:0.###} m and others move {stillError:0.###} m from the expected place.");
        Vector3 low = new(float.PositiveInfinity), high = new(float.NegativeInfinity);
        foreach (int vertex in moved)
        {
            var point = Vector3.Transform(after.Vertices[vertex], placement.World);
            low = Vector3.Min(low, point); high = Vector3.Max(high, point);
        }
        // The leaves' keys name parts of the edited mesh, though its hulls were rebuilt.
        var keys = CollisionPartKey.Of(collision, chosen);
        var edited = plan.Assets;
        catalog.ReadOverride = tag => edited.GetValueOrDefault(tag);
        int refound;
        try
        {
            var (found, missing) = CollisionPartKey.Resolve(new CollisionSceneLoader(catalog).Load(scene), keys);
            Check(found.Count > 0 && missing.Count == 0, $"The edited mesh lost {missing.Count} leaf keys.");
            refound = found.Count;
        }
        finally { catalog.ReadOverride = null; }
        Console.WriteLine($"PASS collision-parts mesh={LeafMesh:X8} parts={parts.Triangles.Count} leaves={leaves.Length} scale={factor:0.###} "
            + $"tags={string.Join(",", plan.Assets.Keys.Select(tag => tag.ToString("X8")))} "
            + $"leaf-box-z={low.Z:0.##}..{high.Z:0.##} moved-error={movedError * 1000:0.#}mm others={stillError * 1000:0.#}mm size=kept "
            + $"twice=refused keys-after-edit={refound}");
        Placements(catalog, collision);
    }

    /**
     * Parts chosen through several placements: one input per shape keeps every move, a shape placed
     * twice refuses two moves of one part, and two shapes write two assets.
     */
    private static void Placements(PackageCatalog catalog, CollisionScene collision)
    {
        bool Editable(CollisionPiece piece) => piece.Shape.Class == CollisionShapes.CompressedMesh && CollisionParts.Of(piece.Shape).Triangles.Count > 1
            && HavokPackfile.Parse(catalog.ReadOriginal(piece.Tag)) is { UserTag: 1 } file && file.RootOffset == piece.Offset;
        var placed = Enumerable.Range(0, collision.Placements.Count).Where(i => collision.Placements[i].Bone < 0)
            .GroupBy(i => collision.Placements[i].Piece).Where(group => Editable(collision.Pieces[group.Key])).ToArray();
        var twice = placed.FirstOrDefault(group => group.Count() > 1
            && collision.Placements[group.ElementAt(0)].World != collision.Placements[group.ElementAt(1)].World);
        if (twice is null) throw new InvalidDataException("The sabre map has no editable mesh placed twice.");
        int a = twice.ElementAt(0), b = twice.ElementAt(1);
        var shape = collision.Pieces[twice.Key].Shape;
        var parts = CollisionParts.Of(shape);
        var grow = Matrix4x4.CreateScale(2);
        var both = CollisionPartEdit.Input(catalog, collision, [new(a, 0), new(b, 1)], grow);
        var first = parts.Vertices(shape, [0]).Except(parts.Vertices(shape, [1])).First();
        var second = parts.Vertices(shape, [1]).Except(parts.Vertices(shape, [0])).First();
        Check(both.Shapes.Count == 1 && both.Shapes[0].Vertices[first] != shape.Vertices[first]
            && both.Shapes[0].Vertices[second] != shape.Vertices[second], "A shape chosen through two placements lost one move.");
        Throws<NotSupportedException>(() => CollisionPartEdit.Input(catalog, collision, [new(a, 0), new(b, 0)], grow));
        var other = placed.First(group => group.Key != twice.Key);
        var lift = Matrix4x4.CreateTranslation(0, 0, .25f);
        var input = CollisionPartEdit.Input(catalog, collision, [new(a, 0), new(other.First(), 0)], lift);
        var plan = CollisionImport.Plan(catalog, collision, input, catalog.ReadOriginal);
        uint[] tags = [collision.Pieces[twice.Key].Tag, collision.Pieces[other.Key].Tag];
        Check(input.Shapes.Count == 2 && tags.All(plan.Assets.ContainsKey), "Parts in two shapes did not write both shapes.");
        Console.WriteLine($"PASS collision-parts-placements shared={tags[0]:X8} placements={twice.Count()} merged=one-input "
            + $"same-part-two-ways=refused second={tags[1]:X8} assets={plan.Assets.Count}");
    }
}
