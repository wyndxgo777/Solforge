using System.Diagnostics;
using System.Numerics;
using SunriseStudio.Packages;
using SunriseStudio.Scene;
using SunriseStudio.Scene.Collision;
using static Checks;

/** Select connected joins touching, near and contained parts in scope; keys find parts again. */
internal static class ConnectivityChecks
{
    private const uint SabreMap = 0x80FAA003, LeafMesh = 0x80B91745, HitboxEntity = 0x80FCC03B;

    /** Runs the checks that use generated fixtures instead of installed game content. */
    internal static void Synthetic()
    {
        var box = CollisionShapes.BoxShape(new(.5f));
        // Boxes sharing a face join; 1 cm apart they join at a 2 cm gap, 5 cm apart they do not.
        Check(Count(Scene((0, box, Vector3.Zero), (0, box, Vector3.UnitX)), new(0, CollisionConnectScope.Entity)) == 2, "Boxes sharing a face did not join.");
        Check(Count(Scene((0, box, Vector3.Zero), (0, box, new(1.01f, 0, 0))), new(.02f, CollisionConnectScope.Entity)) == 2, "Boxes 1 cm apart did not join at a 2 cm gap.");
        Check(Count(Scene((0, box, Vector3.Zero), (0, box, new(1.05f, 0, 0))), new(.02f, CollisionConnectScope.Entity)) == 1, "Boxes 5 cm apart joined at a 2 cm gap.");
        // A small box inside a big one touches no triangle; the big box holds its corner.
        Check(Count(Scene((0, CollisionShapes.BoxShape(new(2)), Vector3.Zero), (0, CollisionShapes.BoxShape(new(.2f)), Vector3.Zero)), new(0, CollisionConnectScope.Entity)) == 2,
            "A box inside a box did not join.");
        // Two triangles that cross, with no corner near the other triangle.
        var flat = Mesh([new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], [0, 1, 2]);
        var upright = Mesh([new(0, -.5f, -1), new(0, -.5f, 1), new(0, .5f, 0)], [0, 1, 2]);
        Check(Count(Scene((0, flat, Vector3.Zero), (0, upright, Vector3.Zero)), new(0, CollisionConnectScope.Entity)) == 2, "Crossing triangles did not join.");
        // Corners 0.06 mm apart round to two weld cells, so there are two parts; they still join.
        var split = Mesh([new(.00002f, 0, 0), new(-1, 0, 0), new(-1, 1, 0), new(.00008f, 0, 0), new(1, 0, 0), new(1, 1, 0)], [0, 1, 2, 3, 4, 5]);
        Check(CollisionParts.Of(split).Triangles.Count == 2, "The weld fixture did not make two parts.");
        Check(Count(Scene((0, split, Vector3.Zero)), new(0, CollisionConnectScope.Shape)) == 2, "Parts across a weld cell edge did not join.");
        // Shapes 3 cm apart with 2 cm convex radii touch at gap 0; without radii they do not.
        CollisionShape Rounded(float radius) => new()
        {
            Class = CollisionShapes.ConvexVertices, Vertices = box.Vertices, Indices = box.Indices, ConvexRadius = radius,
            BoundsMin = box.BoundsMin - new Vector3(radius), BoundsMax = box.BoundsMax + new Vector3(radius)
        };
        Check(Count(Scene((0, Rounded(.02f), Vector3.Zero), (0, Rounded(.02f), new(1.03f, 0, 0))), new(0, CollisionConnectScope.Entity)) == 2, "Convex radii did not count.");
        Check(Count(Scene((0, Rounded(0), Vector3.Zero), (0, Rounded(0), new(1.03f, 0, 0))), new(0, CollisionConnectScope.Entity)) == 1, "Shapes 3 cm apart joined at gap 0.");
        // A shape keeps to its placement, an entity to its entity; touching crosses entities.
        var chain = Scene((0, box, Vector3.Zero), (1, box, Vector3.UnitX), (2, box, new(2, 0, 0)), (0, box, new(9, 0, 0)));
        var shape = Connect(chain, new(.02f, CollisionConnectScope.Shape));
        var entity = Connect(chain, new(.02f, CollisionConnectScope.Entity));
        var touching = Connect(chain, new(.02f, CollisionConnectScope.Touching));
        Check(shape.Parts.All(part => part.Placement == 0), "Shape scope left its placement.");
        Check(entity.Parts.All(part => chain.Placements[part.Placement].Entity == 0) && entity.Parts.Count == 1,
            "Entity scope joined another entity or a far part.");
        Check(touching.Parts.Select(part => part.Placement).SequenceEqual([0, 1, 2]), "Touching scope did not follow the chain.");
        var hidden = CollisionConnectivity.Connected(chain, [new(0, 0)], new(.02f, CollisionConnectScope.Touching),
            index => chain.Placements[index].World, index => index != 1);
        Check(hidden.Parts.Count == 1, "Touching scope crossed a hidden placement.");
        // A shape without triangles is skipped; the part cap stops the search.
        var empty = Scene((0, box, Vector3.Zero), (0, new CollisionShape { Class = "hkpUnknownShape" }, Vector3.Zero));
        Check(Connect(empty, new(.02f, CollisionConnectScope.Entity)).SkippedPlacements == 1, "A shape without triangles was not counted as skipped.");
        Check(Connect(Scene((0, box, Vector3.Zero), (0, box, Vector3.UnitX)), new(.02f, CollisionConnectScope.Entity, 1)).Truncated,
            "The part cap did not stop the search.");
        Keys();
        Console.WriteLine("PASS collision-connect-synthetic face=join gap-1cm=join gap-5cm=apart inside=join crossing=join weld-edge=join "
            + "radius=join scope=shape,entity,touching hidden=apart skipped=1 cap=truncated");
    }

    /** Bodies of one entity differ only by shape offset and bone; a key finds the right one. */
    private static void Keys()
    {
        var box = CollisionShapes.BoxShape(new(.5f));
        var scene = new CollisionScene();
        scene.Entities.Add(new() { Index = 0, Source = CollisionSource.EntityPhysics, Tag = 0x80A00000 });
        scene.Pieces.Add(new(0, 0x80A00001, 0x40, box));
        scene.Pieces.Add(new(1, 0x80A00001, 0x80, box));
        foreach (var (piece, bone) in new[] { (0, 1), (1, 2), (1, 2) })
            scene.Placements.Add(new(0, piece, 0, 0, -1, QsTransform.Identity, Matrix4x4.Identity) { Bone = bone });
        var copy = new CollisionScene();
        copy.Entities.AddRange(scene.Entities); copy.Pieces.AddRange(scene.Pieces);
        copy.Placements.AddRange(scene.Placements.AsEnumerable().Reverse());
        Check(Enumerable.Range(0, 3).All(i => CollisionPartKey.OfPlacement(scene, i).FindPlacement(scene) == i),
            "A placement key did not find its own placement.");
        Check(CollisionPartKey.OfPlacement(scene, 0).FindPlacement(copy) == 2 && CollisionPartKey.OfPlacement(scene, 1).FindPlacement(copy) == 0,
            "A body key found another body after the order changed.");
        var (found, missing) = CollisionPartKey.Resolve(copy, CollisionPartKey.Of(scene, [new(0, 0), new(2, 0)]));
        Check(found.SequenceEqual([new CollisionPartRef(2, 0), new CollisionPartRef(1, 0)]) && missing.Count == 0, "Part keys did not resolve.");
    }

    /** Sabre leaf hulls join; entity bodies keep to their entity and join in the default scope. */
    internal static void Owned(PackageCatalog catalog)
    {
        var collision = new CollisionSceneLoader(catalog).Load(new AssetSceneLoader(catalog).Load(SabreMap));
        int index = Enumerable.Range(0, collision.Placements.Count).Single(i => collision.Pieces[collision.Placements[i].Piece].Tag == LeafMesh);
        var placement = collision.Placements[index];
        var shape = collision.Pieces[placement.Piece].Shape;
        var parts = CollisionParts.Of(shape);
        int[] leaves = CollisionPartChecks.Leaves(collision, index);
        int first50 = Enumerable.Range(0, parts.Triangles.Count).Single(part => parts.Triangles[part][0] == 50);
        int first86 = Enumerable.Range(0, parts.Triangles.Count).Single(part => parts.Triangles[part][0] == 86);
        Matrix4x4 World(int i) => collision.Placements[i].World;
        var clock = Stopwatch.StartNew();
        var connected = CollisionConnectivity.Connected(collision, [new(index, first50)], new(.02f, CollisionConnectScope.Shape), World, _ => true);
        double shapeMs = clock.Elapsed.TotalMilliseconds;
        Check(connected.Parts.Contains(new(index, first86)) && connected.Parts.All(part => part.Placement == index),
            "Overlapping hulls 50 and 86 did not join, or the shape scope left its mesh.");
        clock.Restart();
        var entity = CollisionConnectivity.Connected(collision, [new(index, first50)], new(Scope: CollisionConnectScope.Entity), World, _ => true);
        double entityMs = clock.Elapsed.TotalMilliseconds;
        // The smallest gap that joins all five leaf hulls from the first one.
        float[] gaps = [0, .01f, .02f, .05f, .1f, .2f, .5f];
        float? joinsLeaves = gaps.Cast<float?>().FirstOrDefault(gap => CollisionConnectivity.Connected(collision, [new(index, leaves[0])],
            new(gap!.Value, CollisionConnectScope.Shape), World, _ => true).Parts.Count(part => leaves.Contains(part.Part)) == leaves.Length);
        clock.Restart();
        var touching = CollisionConnectivity.Connected(collision, [new(index, leaves[0])], new(.02f, CollisionConnectScope.Touching), World, _ => true);
        double touchingMs = clock.Elapsed.TotalMilliseconds;
        var reached = touching.Parts.Select(part => part.Placement).Distinct().ToArray();
        Check(reached.Contains(index) && reached.Length > 1, $"Touching scope reached {reached.Length} placements from the leaves.");

        // A placed body keeps to its entity, though Touching scope leaves it from the same seed.
        int Owner(int placement) => collision.Placements[placement].Entity;
        var crossing = Enumerable.Range(0, collision.Placements.Count)
            .Where(i => collision.Entities[Owner(i)].Source == CollisionSource.EntityPhysics
                && collision.Pieces[collision.Placements[i].Piece].Shape.TriangleCount > 0)
            .Select(i => (Seed: i, Near: CollisionConnectivity.Connected(collision, [new(i, 0)],
                new(.02f, CollisionConnectScope.Touching, 4096), World, _ => true)))
            .FirstOrDefault(pair => pair.Near.Parts.Any(part => Owner(part.Placement) != Owner(pair.Seed)));
        Check(crossing.Near is not null, "No placed entity body touches another entity.");
        var placedScope = CollisionConnectivity.DefaultScope(collision, crossing.Seed);
        var placedBodies = CollisionConnectivity.Connected(collision, [new(crossing.Seed, 0)], new(Scope: placedScope), World, _ => true);
        Check(placedScope == CollisionConnectScope.Entity && placedBodies.Parts.All(part => Owner(part.Placement) == Owner(crossing.Seed)),
            "A placed entity body did not default to Entity scope, or Entity scope left the entity.");

        // Keys taken on one load name the same parts on a fresh load.
        var again = new CollisionSceneLoader(catalog).Load(new AssetSceneLoader(catalog).Load(SabreMap));
        var refs = leaves.Select(part => new CollisionPartRef(index, part)).ToArray();
        var (found, missing) = CollisionPartKey.Resolve(again, CollisionPartKey.Of(collision, refs));
        Check(found.SequenceEqual(refs) && missing.Count == 0, "Leaf part keys did not resolve on a fresh load.");

        var npc = new CollisionSceneLoader(catalog).Load(new AssetSceneLoader(catalog).Load(HitboxEntity));
        int body = npc.Placements.FindIndex(p => p.Role == CollisionRole.Hitbox && npc.Pieces[p.Piece].Shape.TriangleCount > 0);
        Check(body >= 0, "The NPC has no hitbox body with triangles.");
        // The default scope for a hitbox joins the bodies it touches.
        var bodies = CollisionConnectivity.Connected(npc, [new(body, 0)], new(Scope: CollisionConnectivity.DefaultScope(npc, body)),
            i => npc.Placements[i].World, _ => true);
        int npcJoined = bodies.Parts.Select(part => part.Placement).Distinct().Count();
        Check(npcJoined > 1, $"Select connected with the default scope joined {npcJoined} NPC bodies.");
        Console.WriteLine($"PASS collision-connect-owned mesh={LeafMesh:X8} parts={parts.Triangles.Count} hulls-50-86=joined "
            + $"shape-scope-parts={connected.Parts.Count} shape-ms={shapeMs:0} entity-scope-parts={entity.Parts.Count} entity-ms={entityMs:0} "
            + $"leaves-join-gap={(joinsLeaves is { } g ? g.ToString("0.###") : "none")} "
            + $"touching-parts={touching.Parts.Count} touching-placements={reached.Length} touching-ms={touchingMs:0} keys=resolved "
            + $"placed-entity-seed={crossing.Seed} placed-entity-bodies={placedBodies.Parts.Count} "
            + $"npc-bodies={npc.Placements.Count} npc-default-joined={npcJoined}");
    }

    private static int Count(CollisionScene scene, CollisionConnectOptions options) => Connect(scene, options).Parts.Count;

    private static CollisionConnectResult Connect(CollisionScene scene, CollisionConnectOptions options) =>
        CollisionConnectivity.Connected(scene, [new(0, 0)], options, index => scene.Placements[index].World, _ => true);

    private static CollisionShape Mesh(Vector3[] vertices, int[] indices) => new()
    {
        Class = CollisionShapes.CompressedMesh, Vertices = vertices, Indices = indices, MeshTriangleCount = indices.Length / 3,
        TriangleKeys = Enumerable.Range(0, indices.Length / 3).Select(i => (uint)(i << 1)).ToArray(),
        BoundsMin = vertices.Aggregate(Vector3.Min), BoundsMax = vertices.Aggregate(Vector3.Max)
    };

    /** One piece per item, placed at a translation, in the given entity. */
    private static CollisionScene Scene(params (int Entity, CollisionShape Shape, Vector3 At)[] items)
    {
        var scene = new CollisionScene();
        for (int e = 0; e <= items.Max(item => item.Entity); e++)
            scene.Entities.Add(new() { Index = e, Source = CollisionSource.Asset, Tag = 0x80A00000u + (uint)e });
        foreach (var (entity, shape, at) in items)
        {
            int piece = scene.Pieces.Count;
            scene.Pieces.Add(new(piece, 0x80B00000u + (uint)piece, 0, shape));
            var world = Matrix4x4.CreateTranslation(at);
            var (min, max) = CollisionScene.WorldBounds(shape, world);
            scene.Placements.Add(new(entity, piece, 0, 0, -1, QsTransform.Identity, world) { BoundsMin = min, BoundsMax = max });
        }
        return scene;
    }
}
