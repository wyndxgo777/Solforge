using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using SunriseStudio.Authoring;
using SunriseStudio.Scene.Collision;
using static Checks;

/**
 * Havok coverage added for build 86657 packfiles, on synthetic packfiles (no game install): hkpListShape (2,785 of
 * 51,261 packfiles), physics-body convexes shipped without planes, Havok Cloth collidables (capsule, tapered
 * capsule, sphere; planes listed), and HavokInspector's survey and OBJ export.
 */
internal static class HavokCoverageChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    internal static void Run()
    {
        ListShape();
        PlanelessConvex();
        Cloth();
        Console.WriteLine("PASS havok-coverage list=merged+mask convex=hull cloth=capsule+tapered+sphere inspector=obj");
    }

    private static void ListShape()
    {
        var builder = new HavokPackfileBuilder();
        var list = new byte[0x90];
        Put(list, 0x50, new Vector3(2, 1, 1));           // aabb half extents
        Put(list, 0x60, Vector3.Zero);                    // aabb centre
        BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(0x70), 0b011); // child 2 disabled
        int root = builder.PlaceObject(list, CollisionShapes.List);
        int rows = builder.Place(new byte[3 * 0x20]);
        builder.WriteArrayHeader(root + 0x30, 3);
        builder.Local(root + 0x30, rows);
        int small = builder.PlaceObject(Box(new Vector3(1, 1, 1)), CollisionShapes.Box);
        int wide = builder.PlaceObject(Box(new Vector3(2, 0.5f, 0.5f)), CollisionShapes.Box);
        var sphere = new byte[0x30]; BinaryPrimitives.WriteSingleLittleEndian(sphere.AsSpan(0x20), 5);
        int disabled = builder.PlaceObject(sphere, CollisionShapes.Sphere);
        builder.Local(rows, small); builder.Local(rows + 0x20, wide); builder.Local(rows + 0x40, disabled);
        var file = HavokPackfile.Parse(builder.Build([(0, CollisionShapes.List), (0, CollisionShapes.Box), (0, CollisionShapes.Sphere)], root, 1));

        var shape = CollisionShapes.Decode(file, root);
        Check(shape.Class == CollisionShapes.List && shape.Undecoded is null, "List shape was not decoded.");
        Check(shape.TriangleCount == 24 && shape.Vertices.Length == 16, $"List shape merged {shape.TriangleCount} triangles, expected two boxes.");
        var min = shape.Vertices.Aggregate(Vector3.Min); var max = shape.Vertices.Aggregate(Vector3.Max);
        Check(Near(min, new(-2, -1, -1)) && Near(max, new(2, 1, 1)), "The disabled sphere child was drawn or a box moved.");
        Check(Near(shape.BoundsMin, new(-2, -1, -1)) && Near(shape.BoundsMax, new(2, 1, 1)), "List bounds do not come from its aabb.");
        Check(CollisionScene.KindOf(shape.Class) == "List", "List shapes have no short kind.");

        var inspection = HavokInspector.Inspect(file);
        Check(inspection.Kind == "shape" && inspection.Placed.Count == 1 && inspection.Shapes.Count == 4
            && inspection.Shapes.All(summary => summary.Error is null && summary.Undecoded is null), "Inspector did not survey the list packfile.");
        var obj = new StringWriter();
        int triangles = HavokInspector.WriteObj(inspection.Placed, obj, "list");
        string[] lines = obj.ToString().Split('\n');
        Check(triangles == 24 && lines.Count(line => line.StartsWith("v ", StringComparison.Ordinal)) == 16
            && lines.Count(line => line.StartsWith("f ", StringComparison.Ordinal)) == 24, "OBJ export lost triangles.");
        Check(lines.Where(line => line.StartsWith("f ", StringComparison.Ordinal))
            .SelectMany(line => line.Trim().Split(' ').Skip(1)).Select(int.Parse).All(index => index is >= 1 and <= 16), "OBJ face indices leave the vertex list.");
    }

    private static void PlanelessConvex()
    {
        Vector3[] tetrahedron = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
        var file = HavokPackfile.Parse(CollisionPackfileWriter.WriteConvexVertices(tetrahedron, [], 0));
        var shape = CollisionShapes.Decode(file, file.RootOffset);
        Check(shape.TriangleCount == 4 && shape.Undecoded is null, $"A convex without planes gave {shape.TriangleCount} triangles, expected its hull.");
    }

    private static void Cloth()
    {
        var builder = new HavokPackfileBuilder();
        int root = builder.PlaceObject(new byte[0x40], ClothCollidables.Container);
        int Collidable(string name, Vector3 translation, int shape)
        {
            var bytes = new byte[0xB0];
            Put(bytes, 0x20, Vector3.UnitX); Put(bytes, 0x30, Vector3.UnitY); Put(bytes, 0x40, Vector3.UnitZ); Put(bytes, 0x50, translation);
            int offset = builder.PlaceObject(bytes, ClothCollidables.Collidable);
            int text = builder.Place(Encoding.ASCII.GetBytes(name + "\0"));
            builder.Local(offset + 0x10, text);
            builder.Local(offset + 0x88, shape);
            return offset;
        }
        var capsule = new byte[0x60];
        Put(capsule, 0x20, new Vector3(-1, 0, 0)); Put(capsule, 0x30, new Vector3(1, 0, 0));
        BinaryPrimitives.WriteSingleLittleEndian(capsule.AsSpan(0x50), 0.5f);
        var tapered = new byte[0xA0];
        Put(tapered, 0x20, Vector3.Zero); Put(tapered, 0x30, new Vector3(0, 0, 1));
        BinaryPrimitives.WriteSingleLittleEndian(tapered.AsSpan(0x90), 0.2f); BinaryPrimitives.WriteSingleLittleEndian(tapered.AsSpan(0x94), 0.4f);
        var sphere = new byte[0x30]; BinaryPrimitives.WriteSingleLittleEndian(sphere.AsSpan(0x2C), 0.25f);
        var plane = new byte[0x30]; Put(plane, 0x20, Vector3.UnitZ);
        Collidable("Collidable_pelvis", new Vector3(1, 2, 3), builder.PlaceObject(capsule, ClothCollidables.Capsule));
        Collidable("Collidable_thigh", Vector3.Zero, builder.PlaceObject(tapered, ClothCollidables.TaperedCapsule));
        Collidable("Collidable_head", Vector3.Zero, builder.PlaceObject(sphere, ClothCollidables.Sphere));
        Collidable("Collidable_ground", Vector3.Zero, builder.PlaceObject(plane, ClothCollidables.Plane));
        var file = HavokPackfile.Parse(builder.Build([(0, ClothCollidables.Container), (0, ClothCollidables.Collidable), (0, ClothCollidables.Capsule),
            (0, ClothCollidables.TaperedCapsule), (0, ClothCollidables.Sphere), (0, ClothCollidables.Plane)], root, 0));

        var collidables = ClothCollidables.Read(file);
        Check(collidables.Select(c => c.Name).SequenceEqual(["Collidable_pelvis", "Collidable_thigh", "Collidable_head", "Collidable_ground"]),
            "Cloth collidable names were not read.");
        Check(Near(collidables[0].Transform.Translation, new(1, 2, 3)) && collidables[0].Transform.M11 == 1, "Collidable transform was not read.");
        Check(collidables[0].Shape is { } capsuleShape && Near(capsuleShape.BoundsMin, new(-1.5f, -0.5f, -0.5f))
            && Near(capsuleShape.BoundsMax, new(1.5f, 0.5f, 0.5f)), "Cloth capsule bounds are wrong.");
        Check(collidables[1].Shape is { } taperedShape && Near(taperedShape.BoundsMin, new(-0.4f, -0.4f, -0.2f))
            && Near(taperedShape.BoundsMax, new(0.4f, 0.4f, 1.4f)) && taperedShape.TriangleCount > 0, "Tapered capsule is wrong.");
        Check(collidables[2].Shape is { } sphereShape && Near(sphereShape.BoundsMax, new(0.25f, 0.25f, 0.25f)), "Cloth sphere radius is not the w lane.");
        Check(collidables[3].Shape is null && collidables[3].ShapeClass == ClothCollidables.Plane, "A cloth plane was drawn.");

        var inspection = HavokInspector.Inspect(file);
        Check(inspection.Kind == "cloth" && inspection.Placed.Count == 3 && inspection.Notes.Any(note => note.Contains(ClothCollidables.Plane, StringComparison.Ordinal)),
            "Inspector did not place the cloth collidables.");
        var placedCapsule = inspection.Placed[0];
        var world = placedCapsule.Shape.Vertices.Select(v => Vector3.Transform(v, placedCapsule.World)).Aggregate(Vector3.Max);
        Check(Near(world, new(2.5f, 2.5f, 3.5f)), "Cloth capsule was not moved to its collidable transform.");
    }

    private static byte[] Box(Vector3 half)
    {
        var bytes = new byte[0x40];
        Put(bytes, 0x30, half);
        return bytes;
    }

    private static void Put(byte[] bytes, int offset, Vector3 value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), value.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 4), value.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 8), value.Z);
    }

    private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;
}
