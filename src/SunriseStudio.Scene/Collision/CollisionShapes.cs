/**
 * File: Decodes Havok collision shape classes (box, capsule, convex, mesh...) from a packfile into CollisionShape.
 * Placement is in CollisionSceneLoader.cs; HavokInspector.cs wraps this for tools.
 */
using System.Numerics;

namespace SunriseStudio.Scene.Collision;

/**
 * One Havok child shape in its own space; three indices form a triangle. Triangles omit the convex
 * radius; bounds include it. Box and convex faces wind outward; mesh triangles keep stored order.
 */
public sealed class CollisionShape
{
    public required string Class { get; init; }
    public Vector3[] Vertices { get; init; } = [];
    public int[] Indices { get; init; } = [];
    public float ConvexRadius { get; init; }
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    /** Triangles from mesh triangles and quads; custom primitive hulls follow them. */
    public int MeshTriangleCount { get; init; }
    /** Shape key of each triangle of a compressed mesh; empty for other shapes. */
    public uint[] TriangleKeys { get; init; } = [];
    /** Convex pieces in a compressed mesh; each adds its hull triangles. */
    public int CustomPrimitives { get; init; }
    /** Custom primitives in a codec or kind the client does not implement; no triangles. */
    public int UndecodedCustomPrimitives { get; init; }
    /** Null when the triangles cover the shape; otherwise the part that has no triangles. */
    public string? Undecoded { get; init; }
    public int TriangleCount => Indices.Length / 3;
}

/** Decodes the child shape classes a static compound points to. */
public static class CollisionShapes
{
    public const string CompressedMesh = "hkpBvCompressedMeshShape";
    public const string ConvexVertices = "hkpConvexVerticesShape";
    public const string Box = "hkpBoxShape";
    public const string Sphere = "hkpSphereShape";
    public const string Capsule = "hkpCapsuleShape";
    public const string Cylinder = "hkpCylinderShape";
    public const string ConvexTranslate = "hkpConvexTranslateShape";
    public const string List = "hkpListShape";

    public const string UndecodedCustom = "custom convex primitive";

    /** The shape at a data offset. An unknown class gives no triangles and no bounds. */
    public static CollisionShape Decode(HavokPackfile file, int offset) => Decode(file, offset, 0);

    /** The local box that encloses the shape object at a data offset, convex radius included. */
    public static (Vector3 Min, Vector3 Max) Bounds(HavokPackfile file, int offset) => Bounds(file, offset, 0);

    /** Decodes the shape object at an offset by its class name; throws when the class is missing. */
    private static CollisionShape Decode(HavokPackfile file, int offset, int depth)
    {
        string type = file.ClassAt(offset) ?? throw new InvalidDataException("Collision shape has no class.");
        switch (type)
        {
            case CompressedMesh: return DecodeCompressedMesh(file, offset);
            case ConvexVertices: return DecodeConvexVertices(file, offset);
            case Box: return DecodeBox(file, offset);
            case ConvexTranslate:
            {
                var child = Decode(file, ConvexChild(file, offset, depth), depth + 1);
                var shift = file.V3(offset + 0x40);
                var bounds = Bounds(file, offset, depth);
                return new CollisionShape
                {
                    Class = type, Vertices = child.Vertices.Select(v => v + shift).ToArray(), Indices = child.Indices,
                    ConvexRadius = file.F32(offset + 0x20), BoundsMin = bounds.Min, BoundsMax = bounds.Max,
                    Undecoded = child.Undecoded
                };
            }
            case List: return DecodeList(file, offset, depth);
            case Sphere:
                return SphereShape(Vector3.Zero, file.F32(offset + 0x20));
            case Capsule:
                // The W lanes repeat the radius and are not part of the points.
                return CapsuleShape(file.V3(offset + 0x30), file.V3(offset + 0x40), file.F32(offset + 0x20));
            default:
                return new CollisionShape { Class = type, Undecoded = type };
        }
    }

    /** Axis-aligned bounds of the shape at an offset (recursing into children up to a depth limit). */
    private static (Vector3 Min, Vector3 Max) Bounds(HavokPackfile file, int offset, int depth)
    {
        string type = file.ClassAt(offset) ?? throw new InvalidDataException("Collision shape has no class.");
        float radius = file.F32(offset + 0x20);
        switch (type)
        {
            case CompressedMesh:
            {
                // The compressed mesh keeps its convex radius at +0x30, not at +0x20.
                var pad = new Vector3(file.F32(offset + 0x30));
                return (file.V3(offset + 0x80) - pad, file.V3(offset + 0x90) + pad);
            }
            case ConvexVertices:
            {
                var half = file.V3(offset + 0x30) + new Vector3(radius);
                var center = file.V3(offset + 0x40);
                return (center - half, center + half);
            }
            case Box:
            {
                var half = file.V3(offset + 0x30) + new Vector3(radius);
                return (-half, half);
            }
            case Sphere:
                return (new Vector3(-radius), new Vector3(radius));
            case Capsule:
            {
                Vector3 a = file.V3(offset + 0x30), b = file.V3(offset + 0x40), pad = new(radius);
                return (Vector3.Min(a, b) - pad, Vector3.Max(a, b) + pad);
            }
            case List:
            {
                // The list keeps the box of its enabled children: half extents at +0x50, centre at +0x60.
                var half = file.V3(offset + 0x50);
                var center = file.V3(offset + 0x60);
                return (center - half, center + half);
            }
            case ConvexTranslate:
            {
                var child = Bounds(file, ConvexChild(file, offset, depth), depth + 1);
                var shift = file.V3(offset + 0x40);
                return (child.Min + shift, child.Max + shift);
            }
            default:
                throw new NotSupportedException($"Collision shape {type} has no decoded bounds.");
        }
    }

    /**
     * hkpListShape (64-bit Havok 2012): the hkpShapeCollection header ends at +0x30, then hkArray of ChildInfo
     * m_childInfo at +0x30 (32-byte rows, child shape pointer at row +0), aabb half extents +0x50, centre +0x60
     * and the 256-bit m_enabledChildren mask at +0x70. Every enabled child is decoded in the list's space and
     * merged; the first undecoded child class is reported. Seen in 2,785 of 51,261 build 86657 packfiles.
     */
    private static CollisionShape DecodeList(HavokPackfile file, int offset, int depth)
    {
        if (depth > 4) throw new InvalidDataException("Collision shape nesting is too deep.");
        var (count, data) = file.Array(offset + 0x30);
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        string? undecoded = null;
        for (int i = 0; i < count; i++)
        {
            bool enabled = i >= 256 || (file.U32(offset + 0x70 + (i >> 5) * 4) >> (i & 31) & 1) != 0;
            if (!enabled) continue;
            if (file.Pointer(data + 0x20 * i) is not int target) { undecoded ??= "list child outside its packfile"; continue; }
            var child = Decode(file, target, depth + 1);
            int start = vertices.Count;
            vertices.AddRange(child.Vertices);
            indices.AddRange(child.Indices.Select(index => start + index));
            undecoded ??= child.Undecoded;
        }
        var bounds = Bounds(file, offset, depth);
        return new CollisionShape
        {
            Class = List, Vertices = vertices.ToArray(), Indices = indices.ToArray(), BoundsMin = bounds.Min, BoundsMax = bounds.Max,
            Undecoded = undecoded
        };
    }

    /** The child sits in a single-shape container at +0x28; its pointer is at +0x30. */
    private static int ConvexChild(HavokPackfile file, int offset, int depth)
    {
        if (depth > 4) throw new InvalidDataException("Collision shape nesting is too deep.");
        return file.Pointer(offset + 0x30) ?? throw new NotSupportedException("Convex translate child is outside its packfile.");
    }

    /**
     * Decodes sections, packed and shared vertices, triangles, quads and custom primitives.
     * @return Shared vertices, then packed vertices, then each custom primitive's vertices. Mesh
     * triangles come first; each custom primitive adds its hull triangles after them.
     */
    private static CollisionShape DecodeCompressedMesh(HavokPackfile file, int offset)
    {
        var mesh = Collision.CompressedMesh.Read(file, offset);
        int sharedCount = mesh.SharedVertices.Length;
        var vertices = new List<Vector3>(sharedCount + mesh.PackedVertices.Length);
        for (int i = 0; i < sharedCount; i++) vertices.Add(mesh.SharedVertex(i));
        var packedStart = new int[mesh.Sections.Count];
        foreach (var (section, s) in mesh.Sections.Select((x, i) => (x, i)))
        {
            packedStart[s] = vertices.Count;
            for (int k = 0; k < section.NumPackedVertices; k++) vertices.Add(mesh.PackedVertex(section, k));
        }
        var indices = new List<int>();
        var keys = new List<uint>();
        var customs = new List<CustomPrimitive>();
        int custom = 0, undecoded = 0;
        for (int s = 0; s < mesh.Sections.Count; s++)
        {
            var section = mesh.Sections[s];
            int Vertex(int k)
            {
                if (k < section.PackedCount) return packedStart[s] + k;
                int index = mesh.SharedNumber(section, k);
                return index < sharedCount ? index : throw new InvalidDataException("Shared vertex index is out of range.");
            }
            for (int p = 0; p < section.PrimitiveCount; p++)
            {
                uint primitive = mesh.Primitive(section, p);
                var type = Collision.CompressedMesh.TypeOf(primitive);
                if (type == CompressedPrimitiveType.Empty) continue;
                if (type == CompressedPrimitiveType.Custom)
                {
                    custom++;
                    if (mesh.DecodeCustom(s, p) is { } decoded) customs.Add(decoded);
                    else undecoded++;
                    continue;
                }
                int a = (int)(primitive & 0xFF), b = (int)(primitive >> 8) & 0xFF, c = (int)(primitive >> 16) & 0xFF, d = (int)(primitive >> 24);
                indices.AddRange([Vertex(a), Vertex(b), Vertex(c)]);
                keys.Add((uint)(s << 8 | p << 1));
                // A quad is (0, 1, 2) for key bit 0 clear and (0, 2, 3) for key bit 0 set.
                if (type != CompressedPrimitiveType.Quad) continue;
                indices.AddRange([Vertex(a), Vertex(c), Vertex(d)]);
                keys.Add((uint)(s << 8 | p << 1 | 1));
            }
        }
        int meshTriangles = indices.Count / 3;
        foreach (var piece in customs)
        {
            var points = piece.Kind == 4 ? CylinderPoints(piece.Vertices[0], piece.Vertices[1], piece.CylinderRadius) : piece.Vertices;
            int start = vertices.Count;
            vertices.AddRange(points);
            int[] hull = ConvexHull.Triangles(points);
            indices.AddRange(hull.Select(i => start + i));
            keys.AddRange(Enumerable.Repeat((uint)(piece.Section << 8 | piece.Primitive << 1), hull.Length / 3));
        }
        var bounds = Bounds(file, offset, 0);
        return new CollisionShape
        {
            Class = CompressedMesh, Vertices = vertices.ToArray(), Indices = indices.ToArray(), ConvexRadius = mesh.ConvexRadius,
            BoundsMin = bounds.Min, BoundsMax = bounds.Max, MeshTriangleCount = meshTriangles, TriangleKeys = keys.ToArray(), CustomPrimitives = custom,
            UndecodedCustomPrimitives = undecoded, Undecoded = undecoded > 0 ? UndecodedCustom : null
        };
    }

    /** Sixteen points on each end circle of a cylinder; the padding radius is left out. */
    private static Vector3[] CylinderPoints(Vector3 a, Vector3 b, float radius)
    {
        var axis = b - a;
        if (axis.LengthSquared() == 0) return [a];
        axis = Vector3.Normalize(axis);
        var u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
        var w = Vector3.Cross(axis, u);
        var points = new List<Vector3>();
        for (int i = 0; i < 16; i++)
        {
            var offset = radius * (MathF.Cos(i * MathF.Tau / 16) * u + MathF.Sin(i * MathF.Tau / 16) * w);
            points.Add(a + offset);
            points.Add(b + offset);
        }
        return points.ToArray();
    }

    /** The cylinder has flat caps; its radius does not extend along the axis. */
    public static CollisionShape CylinderShape(Vector3 a, Vector3 b, float radius)
    {
        var axis = Vector3.Normalize(b - a);
        var radial = new Vector3(MathF.Sqrt(MathF.Max(0, 1 - axis.X * axis.X)),
            MathF.Sqrt(MathF.Max(0, 1 - axis.Y * axis.Y)), MathF.Sqrt(MathF.Max(0, 1 - axis.Z * axis.Z))) * radius;
        var vertices = CylinderPoints(a, b, radius);
        return new CollisionShape
        {
            Class = Cylinder, Vertices = vertices, Indices = ConvexHull.Triangles(vertices),
            BoundsMin = Vector3.Min(a, b) - radial, BoundsMax = Vector3.Max(a, b) + radial
        };
    }

    /** Vertices are stored four to a block as x[4], y[4], z[4]. Faces come from the planes. */
    private static CollisionShape DecodeConvexVertices(HavokPackfile file, int offset)
    {
        var (_, blocks) = file.Array(offset + 0x50);
        int count = file.I32(offset + 0x60);
        var vertices = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            int block = blocks + 48 * (i / 4), lane = 4 * (i % 4);
            vertices[i] = new(file.F32(block + lane), file.F32(block + 16 + lane), file.F32(block + 32 + lane));
        }
        var (planeCount, planes) = file.Array(offset + 0x68);
        var bounds = Bounds(file, offset, 0);
        float size = Math.Max(1, (bounds.Max - bounds.Min).Length());
        var indices = new List<int>();
        for (int p = 0; p < planeCount; p++)
        {
            var normal = file.V3(planes + 16 * p);
            float distance = file.F32(planes + 16 * p + 12);
            if (normal.LengthSquared() < 1e-12f) continue;
            normal = Vector3.Normalize(normal);
            AddFace(vertices, normal, distance, 1e-3f * size, indices);
        }
        // Physics-body convexes (s_physics_component_havok_data) often ship without planes; hull the points instead.
        if (indices.Count == 0 && count >= 3) indices.AddRange(ConvexHull.Triangles(vertices));
        return new CollisionShape
        {
            Class = ConvexVertices, Vertices = vertices, Indices = indices.ToArray(), ConvexRadius = file.F32(offset + 0x20),
            BoundsMin = bounds.Min, BoundsMax = bounds.Max
        };
    }

    /** Fans the vertices that lie on one plane, sorted counterclockwise when seen from outside. */
    private static void AddFace(Vector3[] vertices, Vector3 normal, float distance, float tolerance, List<int> indices)
    {
        var face = new List<int>();
        for (int i = 0; i < vertices.Length; i++)
        {
            if (MathF.Abs(Vector3.Dot(normal, vertices[i]) + distance) > tolerance) continue;
            if (face.All(j => Vector3.DistanceSquared(vertices[j], vertices[i]) > tolerance * tolerance)) face.Add(i);
        }
        if (face.Count < 3) return;
        var center = face.Aggregate(Vector3.Zero, (sum, i) => sum + vertices[i]) / face.Count;
        var u = Vector3.Normalize(vertices[face[0]] - center);
        var w = Vector3.Cross(normal, u);
        var ordered = face.OrderBy(i => MathF.Atan2(Vector3.Dot(vertices[i] - center, w), Vector3.Dot(vertices[i] - center, u)))
            .ThenBy(i => i).ToArray();
        for (int i = 1; i + 1 < ordered.Length; i++) indices.AddRange([ordered[0], ordered[i], ordered[i + 1]]);
    }

    /** Decodes a Havok box shape. */
    private static CollisionShape DecodeBox(HavokPackfile file, int offset)
    {
        var bounds = Bounds(file, offset, 0);
        return BoxShape(file.V3(offset + 0x30), file.F32(offset + 0x20), bounds);
    }

    /** A box about the origin; corner i takes x from bit 0, y from bit 1, z from bit 2. */
    public static CollisionShape BoxShape(Vector3 half) => BoxShape(half, 0, (-half, half));

    /** Builds the eight-corner box shape from half extents and a convex radius. */
    private static CollisionShape BoxShape(Vector3 half, float radius, (Vector3 Min, Vector3 Max) bounds)
    {
        var vertices = Enumerable.Range(0, 8).Select(i => new Vector3((i & 1) == 0 ? -half.X : half.X,
            (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z)).ToArray();
        int[] indices = [0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, 0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, 0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5];
        return new CollisionShape
        {
            Class = Box, Vertices = vertices, Indices = indices, ConvexRadius = radius, BoundsMin = bounds.Min, BoundsMax = bounds.Max
        };
    }

    /** A sphere of the radius about a center; the radius is the whole size, with no convex radius added. */
    public static CollisionShape SphereShape(Vector3 center, float radius) => CapsuleShape(center, center, radius, radius, Sphere);

    // Segments around the axis, and rings from each pole to the equator.
    private const int CapsuleSegments = 16, CapsuleRings = 6;

    /**
     * A cylinder of the radius from a to b with a hemisphere at each end, as one closed mesh.
     * @return Vertices from the pole beyond a, ring by ring, to the pole beyond b.
     */
    public static CollisionShape CapsuleShape(Vector3 a, Vector3 b, float radius) => CapsuleShape(a, b, radius, radius, Capsule);

    /** A capsule whose end spheres differ (Havok Cloth's tapered capsule); the rings join the two hemispheres. */
    public static CollisionShape TaperedCapsuleShape(Vector3 a, float radiusA, Vector3 b, float radiusB, string shapeClass) =>
        CapsuleShape(a, b, radiusA, radiusB, shapeClass);

    /** Builds a capsule (or cylinder) shape between two axis points with one or two radii. */
    private static CollisionShape CapsuleShape(Vector3 a, Vector3 b, float radius, float radiusB, string shapeClass)
    {
        var line = b - a;
        var axis = line.LengthSquared() > 1e-12f ? Vector3.Normalize(line) : Vector3.UnitZ;
        var u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
        var w = Vector3.Cross(axis, u);
        var vertices = new List<Vector3> { a - axis * radius };
        void Ring(Vector3 center, float ringRadius)
        {
            for (int s = 0; s < CapsuleSegments; s++)
            {
                float angle = s * MathF.Tau / CapsuleSegments;
                vertices.Add(center + ringRadius * (MathF.Cos(angle) * u + MathF.Sin(angle) * w));
            }
        }
        for (int ring = 1; ring <= CapsuleRings; ring++)
        {
            float phi = MathF.PI / 2 * ring / CapsuleRings;
            Ring(a - axis * (radius * MathF.Cos(phi)), radius * MathF.Sin(phi));
        }
        for (int ring = CapsuleRings; ring >= 1; ring--)
        {
            float phi = MathF.PI / 2 * ring / CapsuleRings;
            Ring(b + axis * (radiusB * MathF.Cos(phi)), radiusB * MathF.Sin(phi));
        }
        vertices.Add(b + axis * radiusB);
        int rings = 2 * CapsuleRings, last = vertices.Count - 1;
        var indices = new List<int>();
        for (int s = 0; s < CapsuleSegments; s++)
        {
            int next = (s + 1) % CapsuleSegments;
            indices.AddRange([0, 1 + next, 1 + s]);
            for (int ring = 0; ring + 1 < rings; ring++)
            {
                int here = 1 + ring * CapsuleSegments, there = here + CapsuleSegments;
                indices.AddRange([here + s, here + next, there + next, here + s, there + next, there + s]);
            }
            int end = 1 + (rings - 1) * CapsuleSegments;
            indices.AddRange([end + s, end + next, last]);
        }
        Vector3 padA = new(radius), padB = new(radiusB);
        return new CollisionShape
        {
            Class = shapeClass, Vertices = vertices.ToArray(), Indices = indices.ToArray(),
            BoundsMin = Vector3.Min(a - padA, b - padB), BoundsMax = Vector3.Max(a + padA, b + padB)
        };
    }
}
