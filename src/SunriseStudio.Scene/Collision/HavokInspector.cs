using System.Globalization;
using System.Numerics;

namespace SunriseStudio.Scene.Collision;

/** One shape object of a packfile, decoded on its own (in its own space). */
public sealed record HavokShapeSummary(int Offset, string Class, int Vertices, int Triangles, Vector3 Min, Vector3 Max,
    string? Undecoded, string? Error);

/** A shape the packfile places by itself: compound instances, array entries, cloth collidables or the root shape. */
public sealed record HavokPlacedShape(string Name, int Offset, CollisionShape Shape, Matrix4x4 World);

/** What a Havok packfile tag holds, for the MCP havok tools and exports. */
public sealed record HavokInspection(int UserTag, string RootClass, int RootOffset, string Kind,
    IReadOnlyDictionary<string, int> ClassCounts, IReadOnlyList<(int Offset, string Class)> Objects,
    IReadOnlyList<(int Field, string Name)> Imports, IReadOnlyList<HavokShapeSummary> Shapes,
    IReadOnlyList<ClothCollidable> Cloth, IReadOnlyList<HavokPlacedShape> Placed, IReadOnlyList<string> Notes);

/**
 * Read-only survey of one Havok 2012 packfile (tag type 26/7): every object, every collision shape decoded with
 * CollisionShapes, cloth collidables, and the shapes the file places by itself (what the collision viewport draws
 * when the tag is opened). Build 86657 packfile roots: hkpStaticCompoundShape (map collision), s_hkpShape_array_data
 * (trigger shapes), s_physics_component_havok_data (entity rigid bodies, ragdoll constraints), hclClothContainer
 * (cloth), or a single shape (per-mesh child). Constraints and cloth simulation data are listed, not decoded.
 */
public static class HavokInspector
{
    public static readonly IReadOnlySet<string> ShapeClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        CollisionShapes.CompressedMesh, CollisionShapes.ConvexVertices, CollisionShapes.Box, CollisionShapes.Sphere,
        CollisionShapes.Capsule, CollisionShapes.Cylinder, CollisionShapes.ConvexTranslate, CollisionShapes.List
    };

    public static HavokInspection Inspect(HavokPackfile file, bool decodeShapes = true)
    {
        var objects = file.Objects.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)).ToList();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, type) in objects) counts[type] = counts.GetValueOrDefault(type) + 1;
        var shapes = new List<HavokShapeSummary>();
        if (decodeShapes)
            foreach (var (offset, type) in objects)
            {
                if (!ShapeClasses.Contains(type)) continue;
                try
                {
                    var shape = CollisionShapes.Decode(file, offset);
                    shapes.Add(new(offset, type, shape.Vertices.Length, shape.TriangleCount, shape.BoundsMin, shape.BoundsMax, shape.Undecoded, null));
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException)
                {
                    shapes.Add(new(offset, type, 0, 0, default, default, null, error.Message));
                }
            }
        var notes = new List<string>();
        var cloth = file.RootClass == ClothCollidables.Container ? ClothCollidables.Read(file) : [];
        var placed = Placed(file, cloth, notes);
        int constraints = counts.Where(pair => pair.Key.EndsWith("ConstraintData", StringComparison.Ordinal)).Sum(pair => pair.Value);
        if (constraints > 0) notes.Add($"{constraints} rigid-body constraints (ragdoll, hinge...) listed, not decoded");
        string kind = file.RootClass switch
        {
            StaticCompoundShape.ClassName => "static compound",
            CollisionSceneLoader.ShapeArrayClass => "shape array",
            CollisionSceneLoader.PhysicsDataClass => "object physics",
            ClothCollidables.Container => "cloth",
            var root when ShapeClasses.Contains(root) => "shape",
            _ => "other"
        };
        return new HavokInspection(file.UserTag, file.RootClass, file.RootOffset, kind, counts, objects,
            file.Imports.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)).ToList(), shapes, cloth, placed, notes);
    }

    /** The shapes the packfile places without other tags; imported compound children are noted and left out. */
    public static IReadOnlyList<HavokPlacedShape> Placed(HavokPackfile file, IReadOnlyList<ClothCollidable>? cloth, List<string> notes)
    {
        var placed = new List<HavokPlacedShape>();
        void Shape(string name, int offset, Matrix4x4 world)
        {
            try { placed.Add(new(name, offset, CollisionShapes.Decode(file, offset), world)); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException)
            { notes.Add($"{name} at 0x{offset:X}: {error.Message}"); }
        }
        void Compound(string name, int offset, Matrix4x4 parent)
        {
            int imported = 0;
            foreach (var instance in StaticCompoundShape.Parse(file, offset).Instances)
            {
                if (instance.ChildObject is int child) Shape($"{name}.{instance.Index}", child, instance.Transform.ToMatrix() * parent);
                else imported++;
            }
            if (imported > 0) notes.Add($"{name}: {imported} instances import shapes from other packfiles (named by the owner tag); not included");
        }
        void Array(string name, int field)
        {
            var (count, data) = file.Array(field);
            for (int i = 0; i < count; i++)
            {
                if (file.Pointer(data + 8 * i) is not int target) { notes.Add($"{name}.{i}: shape outside its packfile"); continue; }
                if (file.ClassAt(target) == StaticCompoundShape.ClassName) Compound($"{name}.{i}", target, Matrix4x4.Identity);
                else Shape($"{name}.{i}", target, Matrix4x4.Identity);
            }
        }
        switch (file.RootClass)
        {
            case StaticCompoundShape.ClassName: Compound("compound", file.RootOffset, Matrix4x4.Identity); break;
            case CollisionSceneLoader.ShapeArrayClass: Array("array", file.RootOffset); break;
            case CollisionSceneLoader.PhysicsDataClass:
                Array("body", file.RootOffset + 0x20);
                notes.Add("Object physics: bodies in their own space (bone placement needs the entity)");
                break;
            case ClothCollidables.Container:
                foreach (var collidable in cloth ?? ClothCollidables.Read(file))
                {
                    if (collidable.Shape is { } shape) placed.Add(new(collidable.Name, collidable.ShapeOffset, shape, collidable.Transform));
                    else notes.Add($"{collidable.Name}: {collidable.ShapeClass} not drawn");
                }
                notes.Add("Cloth collidables at their rest transforms");
                break;
            case var root when ShapeClasses.Contains(root): Shape("shape", file.RootOffset, Matrix4x4.Identity); break;
            default: notes.Add($"Root {file.RootClass} places no collision shapes"); break;
        }
        return placed;
    }

    /** Wavefront OBJ of the placed shapes, one object each. @return Triangles written. */
    public static int WriteObj(IReadOnlyList<HavokPlacedShape> placed, TextWriter writer, string title)
    {
        var c = CultureInfo.InvariantCulture;
        writer.WriteLine($"# {title} - Havok collision exported by Solforge (read-only decode)");
        int baseVertex = 1, triangles = 0;
        foreach (var item in placed)
        {
            if (item.Shape.TriangleCount == 0) continue;
            writer.WriteLine($"o {item.Name.Replace(' ', '_')}_{CollisionScene.KindOf(item.Shape.Class)}");
            foreach (var vertex in item.Shape.Vertices)
            {
                var v = Vector3.Transform(vertex, item.World);
                writer.WriteLine(string.Format(c, "v {0:R} {1:R} {2:R}", v.X, v.Y, v.Z));
            }
            var indices = item.Shape.Indices;
            for (int i = 0; i + 2 < indices.Length; i += 3)
                writer.WriteLine(string.Format(c, "f {0} {1} {2}", baseVertex + indices[i], baseVertex + indices[i + 1], baseVertex + indices[i + 2]));
            baseVertex += item.Shape.Vertices.Length;
            triangles += item.Shape.TriangleCount;
        }
        return triangles;
    }
}
