using System.Numerics;
using System.Text;

namespace SunriseStudio.Scene.Collision;

/** One Havok Cloth collidable: its name, rest transform and shape (null when the shape class is not decoded). */
public readonly record struct ClothCollidable(int Offset, string Name, Matrix4x4 Transform, int ShapeOffset, string ShapeClass,
    CollisionShape? Shape);

/**
 * Havok Cloth (hcl) collision in a cloth packfile (root hclClothContainer). Layouts read from build 86657 packfiles
 * (64-bit Havok 2012), checked against the stored derived fields:
 * hclCollidable: name pointer +0x10, hkTransform +0x20 (rotation columns +0x20/+0x30/+0x40, translation +0x50),
 * shape pointer +0x88. hclCapsuleShape: start +0x20, end +0x30, radius +0x50 (+0x54 is 1 / length squared).
 * hclTaperedCapsuleShape: small centre +0x20, big centre +0x30, small radius +0x90, big radius +0x94.
 * hclSphereShape: centre xyz and radius w at +0x20. hclPlaneShape and hclConvexGeometryShape are listed, not drawn.
 */
public static class ClothCollidables
{
    public const string Container = "hclClothContainer", Collidable = "hclCollidable";
    public const string Capsule = "hclCapsuleShape", TaperedCapsule = "hclTaperedCapsuleShape", Sphere = "hclSphereShape";
    public const string Plane = "hclPlaneShape", ConvexGeometry = "hclConvexGeometryShape";

    /** Every collidable in the packfile, in data order. */
    public static IReadOnlyList<ClothCollidable> Read(HavokPackfile file)
    {
        var list = new List<ClothCollidable>();
        foreach (var (offset, type) in file.Objects.OrderBy(pair => pair.Key))
        {
            if (type != Collidable) continue;
            string name = file.Pointer(offset + 0x10) is int text ? CString(file, text) : "";
            var transform = new Matrix4x4(
                file.F32(offset + 0x20), file.F32(offset + 0x24), file.F32(offset + 0x28), 0,
                file.F32(offset + 0x30), file.F32(offset + 0x34), file.F32(offset + 0x38), 0,
                file.F32(offset + 0x40), file.F32(offset + 0x44), file.F32(offset + 0x48), 0,
                file.F32(offset + 0x50), file.F32(offset + 0x54), file.F32(offset + 0x58), 1);
            int shapeOffset = file.Pointer(offset + 0x88) ?? -1;
            string shapeClass = shapeOffset >= 0 ? file.ClassAt(shapeOffset) ?? "" : "";
            list.Add(new(offset, name, transform, shapeOffset, shapeClass, shapeOffset >= 0 ? DecodeShape(file, shapeOffset) : null));
        }
        return list;
    }

    /** A cloth shape in the collidable's space, or null for planes, convex geometry and unknown classes. */
    public static CollisionShape? DecodeShape(HavokPackfile file, int offset)
    {
        switch (file.ClassAt(offset))
        {
            case Capsule:
                return CollisionShapes.CapsuleShape(file.V3(offset + 0x20), file.V3(offset + 0x30), file.F32(offset + 0x50));
            case Sphere:
                return CollisionShapes.SphereShape(file.V3(offset + 0x20), file.F32(offset + 0x2C));
            case TaperedCapsule:
                return CollisionShapes.TaperedCapsuleShape(file.V3(offset + 0x20), file.F32(offset + 0x90), file.V3(offset + 0x30),
                    file.F32(offset + 0x94), TaperedCapsule);
            default:
                return null;
        }
    }

    private static string CString(HavokPackfile file, int offset)
    {
        var text = new StringBuilder();
        for (int i = offset; i < offset + 256; i++)
        {
            byte value;
            try { value = file.U8(i); }
            catch (InvalidDataException) { break; }
            if (value == 0) break;
            text.Append((char)value);
        }
        return text.ToString();
    }
}
