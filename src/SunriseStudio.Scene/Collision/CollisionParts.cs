using System.Numerics;
using System.Runtime.CompilerServices;

namespace SunriseStudio.Scene.Collision;

/**
 * The separate pieces of one shape: triangles joined where they share a vertex position or a shape
 * key. A custom primitive's hull is one piece unless it touches another; a vertex is in one part.
 */
public sealed class CollisionParts
{
    // Positions closer than this are one vertex; decoded copies of a shared vertex land on the same point.
    private const float WeldStep = 1e-4f;
    private static readonly ConditionalWeakTable<CollisionShape, CollisionParts> cache = [];

    /** Part index of each triangle. */
    public int[] PartOfTriangle { get; }
    /** Triangle indices of each part, ascending. */
    public IReadOnlyList<int[]> Triangles { get; }

    private CollisionParts(int[] partOfTriangle, IReadOnlyList<int[]> triangles) =>
        (PartOfTriangle, Triangles) = (partOfTriangle, triangles);

    /** Splits the shape's triangles by part, cached per shape. */
    public static CollisionParts Of(CollisionShape shape) => cache.GetValue(shape, Build);

    /** True when every triangle of the part is a custom primitive hull triangle. */
    public bool IsPrimitive(CollisionShape shape, int part) => Triangles[part].All(triangle => triangle >= shape.MeshTriangleCount);

    /** Stands for every part of a shape that has no shape keys. */
    public const uint WholeShape = uint.MaxValue;

    /** The part's smallest shape key. A vertex move keeps the keys, so it finds the part again. */
    public uint Key(CollisionShape shape, int part) =>
        shape.TriangleKeys.Length < shape.TriangleCount ? WholeShape : Triangles[part].Min(triangle => shape.TriangleKeys[triangle]);

    /** The part that holds a triangle with this key; -1 when none does. */
    public int PartOfKey(CollisionShape shape, uint key)
    {
        if (partByKey is null)
        {
            var map = new Dictionary<uint, int>();
            for (int triangle = 0; triangle < shape.TriangleKeys.Length && triangle < PartOfTriangle.Length; triangle++)
                map.TryAdd(shape.TriangleKeys[triangle], PartOfTriangle[triangle]);
            partByKey = map;
        }
        return partByKey.GetValueOrDefault(key, -1);
    }

    private Dictionary<uint, int>? partByKey;

    /** Every vertex index the parts' triangles use. */
    public IReadOnlySet<int> Vertices(CollisionShape shape, IEnumerable<int> parts)
    {
        var vertices = new HashSet<int>();
        foreach (int part in parts)
            foreach (int triangle in Triangles[part])
                for (int corner = 0; corner < 3; corner++) vertices.Add(shape.Indices[triangle * 3 + corner]);
        return vertices;
    }

    /** The world box of the parts' vertices under a placement transform. */
    public (Vector3 Min, Vector3 Max) Bounds(CollisionShape shape, IEnumerable<int> parts, Matrix4x4 world)
    {
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (int vertex in Vertices(shape, parts))
        {
            var point = Vector3.Transform(shape.Vertices[vertex], world);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
        return (min, max);
    }

    private static CollisionParts Build(CollisionShape shape)
    {
        var weld = new Dictionary<(long, long, long), int>();
        var welded = new int[shape.Vertices.Length];
        for (int i = 0; i < welded.Length; i++)
        {
            var point = shape.Vertices[i];
            var key = ((long)MathF.Round(point.X / WeldStep), (long)MathF.Round(point.Y / WeldStep), (long)MathF.Round(point.Z / WeldStep));
            if (!weld.TryGetValue(key, out int id)) weld[key] = id = weld.Count;
            welded[i] = id;
        }
        int count = shape.TriangleCount;
        var parent = Enumerable.Range(0, count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var first = new Dictionary<int, int>();
        for (int triangle = 0; triangle < count; triangle++)
            for (int corner = 0; corner < 3; corner++)
            {
                int id = welded[shape.Indices[triangle * 3 + corner]];
                if (first.TryGetValue(id, out int other)) parent[Find(triangle)] = Find(other);
                else first[id] = triangle;
            }
        // Every hull triangle of one custom primitive, and both halves of a quad, carry the same shape key.
        var byKey = new Dictionary<uint, int>();
        for (int triangle = 0; triangle < count && triangle < shape.TriangleKeys.Length; triangle++)
        {
            if (byKey.TryGetValue(shape.TriangleKeys[triangle], out int other)) parent[Find(triangle)] = Find(other);
            else byKey[shape.TriangleKeys[triangle]] = triangle;
        }
        var groups = Enumerable.Range(0, count).GroupBy(Find).OrderBy(group => group.Min()).Select(group => group.ToArray()).ToArray();
        var partOf = new int[count];
        for (int part = 0; part < groups.Length; part++)
            foreach (int triangle in groups[part]) partOf[triangle] = part;
        return new(partOf, groups);
    }
}
