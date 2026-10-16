using System.Numerics;

namespace SunriseStudio.Formats;

/** An integer voxel box, minimum inclusive and maximum exclusive, on a grid of cubes from an origin. */
public readonly record struct TomeVoxelBox(int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ)
{
    public bool IsEmpty => MinX >= MaxX || MinY >= MaxY || MinZ >= MaxZ;
    public int SizeX => MaxX - MinX;
    public int SizeY => MaxY - MinY;
    public int SizeZ => MaxZ - MinZ;
    public long Count => IsEmpty ? 0 : (long)SizeX * SizeY * SizeZ;
    /** Lower bound of the box on an axis. */
    public int Min(int axis) => axis == 0 ? MinX : axis == 1 ? MinY : MinZ;
    /** Upper bound (exclusive) of the box on an axis. */
    public int Max(int axis) => axis == 0 ? MaxX : axis == 1 ? MaxY : MaxZ;

    /** Overlap of two boxes. */
    public TomeVoxelBox Intersect(TomeVoxelBox other) => new(Math.Max(MinX, other.MinX), Math.Max(MinY, other.MinY),
        Math.Max(MinZ, other.MinZ), Math.Min(MaxX, other.MaxX), Math.Min(MaxY, other.MaxY), Math.Min(MaxZ, other.MaxZ));

    /** Smallest box covering both; an empty box is ignored. */
    public TomeVoxelBox Union(TomeVoxelBox other) => IsEmpty ? other : other.IsEmpty ? this : new(Math.Min(MinX, other.MinX),
        Math.Min(MinY, other.MinY), Math.Min(MinZ, other.MinZ), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY), Math.Max(MaxZ, other.MaxZ));

    /** The box grown by the given amount on every side. */
    public TomeVoxelBox Grow(int by) => new(MinX - by, MinY - by, MinZ - by, MaxX + by, MaxY + by, MaxZ + by);
    /** True when the voxel is inside the box (maximum exclusive). */
    public bool Contains(int x, int y, int z) => x >= MinX && x < MaxX && y >= MinY && y < MaxY && z >= MinZ && z < MaxZ;
}

/** A dense occupancy grid over one voxel box. */
public sealed class TomeVoxelGrid
{
    private readonly bool[] cells;

    public TomeVoxelGrid(TomeVoxelBox box)
    {
        if (box.Count > 64_000_000) throw new InvalidDataException("The occlusion edit region is too large.");
        Box = box;
        cells = new bool[box.Count];
    }

    public TomeVoxelBox Box { get; }
    public bool this[int x, int y, int z]
    {
        get => Box.Contains(x, y, z) && cells[Index(x, y, z)];
        set { if (Box.Contains(x, y, z)) cells[Index(x, y, z)] = value; }
    }

    private long Index(int x, int y, int z) => ((long)(z - Box.MinZ) * Box.SizeY + (y - Box.MinY)) * Box.SizeX + (x - Box.MinX);
}

/** Marks the voxels a triangle surface passes through, by the separating-axis box and triangle test. */
public static class TomeVoxelizer
{
    /**
     * The voxel box that covers a set of triangles.
     * @param triangles Three positions per triangle, world space.
     * @param origin Grid origin.
     * @param size Voxel edge length.
     * @return The covering box; empty for no triangles.
     */
    public static TomeVoxelBox Cover(IReadOnlyList<Vector3> triangles, Vector3 origin, float size)
    {
        var box = new TomeVoxelBox(0, 0, 0, 0, 0, 0);
        for (int i = 0; i + 2 < triangles.Count; i += 3)
            box = box.Union(Cover(triangles[i], triangles[i + 1], triangles[i + 2], origin, size));
        return box;
    }

    /**
     * Marks every voxel inside the grid that a triangle touches.
     * @param grid Grid to mark.
     * @param triangles Three positions per triangle, world space.
     * @param origin Grid origin.
     * @param size Voxel edge length.
     */
    public static void Mark(TomeVoxelGrid grid, IReadOnlyList<Vector3> triangles, Vector3 origin, float size)
    {
        var half = new Vector3(size * 0.5f);
        for (int t = 0; t + 2 < triangles.Count; t += 3)
        {
            Vector3 a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            if (!float.IsFinite(a.X + a.Y + a.Z + b.X + b.Y + b.Z + c.X + c.Y + c.Z))
                throw new InvalidDataException("An occluder triangle is not finite.");
            var box = Cover(a, b, c, origin, size).Intersect(grid.Box);
            for (int z = box.MinZ; z < box.MaxZ; z++)
                for (int y = box.MinY; y < box.MaxY; y++)
                    for (int x = box.MinX; x < box.MaxX; x++)
                    {
                        var center = origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * size;
                        if (Overlaps(center, half, a, b, c)) grid[x, y, z] = true;
                    }
        }
    }

    /**
     * Marks one mesh as a solid: its surface voxels, then every voxel its surface encloses.
     * @param grid Grid to mark.
     * @param mesh Three positions per triangle of one mesh, world space.
     * @param origin Grid origin.
     * @param size Voxel edge length.
     */
    public static void MarkSolid(TomeVoxelGrid grid, IReadOnlyList<Vector3> mesh, Vector3 origin, float size)
    {
        // The fill runs past the grid so faces on the grid's edge still close the mesh.
        var area = Cover(mesh, origin, size).Grow(1).Intersect(grid.Box.Grow(2));
        if (area.IsEmpty || area.Intersect(grid.Box).IsEmpty) return;
        var surface = new TomeVoxelGrid(area);
        Mark(surface, mesh, origin, size);
        // Voxels the outside cannot reach without crossing the surface are inside the mesh.
        var outside = new TomeVoxelGrid(area);
        var queue = new Queue<(int X, int Y, int Z)>();
        for (int z = area.MinZ; z < area.MaxZ; z++)
            for (int y = area.MinY; y < area.MaxY; y++)
                for (int x = area.MinX; x < area.MaxX; x++)
                    if ((x == area.MinX || y == area.MinY || z == area.MinZ || x == area.MaxX - 1 || y == area.MaxY - 1 || z == area.MaxZ - 1)
                        && !surface[x, y, z] && !outside[x, y, z]) { outside[x, y, z] = true; queue.Enqueue((x, y, z)); }
        var steps = new (int, int, int)[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) };
        while (queue.Count > 0)
        {
            var (x, y, z) = queue.Dequeue();
            foreach (var (dx, dy, dz) in steps)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (!area.Contains(nx, ny, nz) || surface[nx, ny, nz] || outside[nx, ny, nz]) continue;
                outside[nx, ny, nz] = true;
                queue.Enqueue((nx, ny, nz));
            }
        }
        for (int z = area.MinZ; z < area.MaxZ; z++)
            for (int y = area.MinY; y < area.MaxY; y++)
                for (int x = area.MinX; x < area.MaxX; x++)
                    if (!outside[x, y, z]) grid[x, y, z] = true;
    }

    /** A copy of a grid with every voxel next to a marked one, across a face, marked too. */
    public static TomeVoxelGrid Dilate(TomeVoxelGrid grid)
    {
        var box = grid.Box;
        var result = new TomeVoxelGrid(box);
        for (int z = box.MinZ; z < box.MaxZ; z++)
            for (int y = box.MinY; y < box.MaxY; y++)
                for (int x = box.MinX; x < box.MaxX; x++)
                    result[x, y, z] = grid[x, y, z] || grid[x - 1, y, z] || grid[x + 1, y, z] || grid[x, y - 1, z]
                        || grid[x, y + 1, z] || grid[x, y, z - 1] || grid[x, y, z + 1];
        return result;
    }

    /**
     * Marks the voxels whose centre lies inside a mesh. Space the outside reaches through a hole
     * stays unmarked.
     * @param grid Grid to mark.
     * @param mesh Three positions per triangle of one mesh, world space.
     * @param origin Grid origin.
     * @param size Voxel edge length.
     */
    public static void MarkInside(TomeVoxelGrid grid, IReadOnlyList<Vector3> mesh, Vector3 origin, float size)
    {
        var area = Cover(mesh, origin, size).Grow(1).Intersect(grid.Box.Grow(2));
        if (area.IsEmpty || area.Intersect(grid.Box).IsEmpty) return;
        // The segment between two neighbouring centres stays in their two voxels, so only triangles
        // touching one of them can cut it.
        var touching = new Dictionary<(int, int, int), List<int>>();
        var half = new Vector3(size * 0.5f);
        for (int t = 0; t + 2 < mesh.Count; t += 3)
        {
            Vector3 a = mesh[t], b = mesh[t + 1], c = mesh[t + 2];
            if (!float.IsFinite(a.X + a.Y + a.Z + b.X + b.Y + b.Z + c.X + c.Y + c.Z))
                throw new InvalidDataException("An occluder triangle is not finite.");
            var box = Cover(a, b, c, origin, size).Intersect(area);
            for (int z = box.MinZ; z < box.MaxZ; z++)
                for (int y = box.MinY; y < box.MaxY; y++)
                    for (int x = box.MinX; x < box.MaxX; x++)
                        if (Overlaps(origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * size, half, a, b, c))
                        {
                            if (!touching.TryGetValue((x, y, z), out var list)) touching[(x, y, z)] = list = [];
                            list.Add(t);
                        }
        }
        var outside = new TomeVoxelGrid(area);
        var queue = new Queue<(int X, int Y, int Z)>();
        for (int z = area.MinZ; z < area.MaxZ; z++)
            for (int y = area.MinY; y < area.MaxY; y++)
                for (int x = area.MinX; x < area.MaxX; x++)
                    if (x == area.MinX || y == area.MinY || z == area.MinZ || x == area.MaxX - 1 || y == area.MaxY - 1 || z == area.MaxZ - 1)
                    { outside[x, y, z] = true; queue.Enqueue((x, y, z)); }
        var steps = new (int, int, int)[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) };
        while (queue.Count > 0)
        {
            var (x, y, z) = queue.Dequeue();
            var from = origin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * size;
            foreach (var (dx, dy, dz) in steps)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (!area.Contains(nx, ny, nz) || outside[nx, ny, nz]) continue;
                var to = from + new Vector3(dx, dy, dz) * size;
                if (Cuts(touching.GetValueOrDefault((x, y, z)), from, to) || Cuts(touching.GetValueOrDefault((nx, ny, nz)), from, to)) continue;
                outside[nx, ny, nz] = true;
                queue.Enqueue((nx, ny, nz));
            }
        }
        for (int z = area.MinZ; z < area.MaxZ; z++)
            for (int y = area.MinY; y < area.MaxY; y++)
                for (int x = area.MinX; x < area.MaxX; x++)
                    if (!outside[x, y, z]) grid[x, y, z] = true;

        bool Cuts(List<int>? triangles, Vector3 p, Vector3 q)
        {
            if (triangles is null) return false;
            foreach (int t in triangles)
                if (SegmentCrosses(p, q, mesh[t], mesh[t + 1], mesh[t + 2])) return true;
            return false;
        }
    }

    /** True when a segment meets a triangle; edges count, so a shared edge leaks nothing. */
    private static bool SegmentCrosses(Vector3 p, Vector3 q, Vector3 a, Vector3 b, Vector3 c)
    {
        const float Slack = 1e-5f;
        Vector3 d = q - p, e1 = b - a, e2 = c - a, h = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, h);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1 / det;
        Vector3 s = p - a;
        float u = Vector3.Dot(s, h) * inv;
        if (u < -Slack || u > 1 + Slack) return false;
        var k = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, k) * inv;
        if (v < -Slack || u + v > 1 + Slack) return false;
        float t = Vector3.Dot(e2, k) * inv;
        return t >= -Slack && t <= 1 + Slack;
    }

    private static TomeVoxelBox Cover(Vector3 a, Vector3 b, Vector3 c, Vector3 origin, float size)
    {
        var min = (Vector3.Min(a, Vector3.Min(b, c)) - origin) / size;
        var max = (Vector3.Max(a, Vector3.Max(b, c)) - origin) / size;
        return new((int)MathF.Floor(min.X), (int)MathF.Floor(min.Y), (int)MathF.Floor(min.Z),
            (int)MathF.Floor(max.X) + 1, (int)MathF.Floor(max.Y) + 1, (int)MathF.Floor(max.Z) + 1);
    }

    /** True when a triangle and an axis-aligned box share a point; touching counts. */
    internal static bool Overlaps(Vector3 center, Vector3 half, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 v0 = a - center, v1 = b - center, v2 = c - center;
        Vector3 e0 = v1 - v0, e1 = v2 - v1, e2 = v0 - v2;
        foreach (var edge in new[] { e0, e1, e2 })
            if (Separated(new Vector3(0, -edge.Z, edge.Y)) || Separated(new Vector3(edge.Z, 0, -edge.X))
                || Separated(new Vector3(-edge.Y, edge.X, 0))) return false;
        for (int axis = 0; axis < 3; axis++)
        {
            float lo = Math.Min(Get(v0, axis), Math.Min(Get(v1, axis), Get(v2, axis)));
            float hi = Math.Max(Get(v0, axis), Math.Max(Get(v1, axis), Get(v2, axis)));
            if (lo > Get(half, axis) || hi < -Get(half, axis)) return false;
        }
        var normal = Vector3.Cross(e0, e1);
        float d = Vector3.Dot(normal, v0);
        float r = half.X * MathF.Abs(normal.X) + half.Y * MathF.Abs(normal.Y) + half.Z * MathF.Abs(normal.Z);
        return MathF.Abs(d) <= r;

        bool Separated(Vector3 axis)
        {
            float p0 = Vector3.Dot(v0, axis), p1 = Vector3.Dot(v1, axis), p2 = Vector3.Dot(v2, axis);
            float r = half.X * MathF.Abs(axis.X) + half.Y * MathF.Abs(axis.Y) + half.Z * MathF.Abs(axis.Z);
            return Math.Min(p0, Math.Min(p1, p2)) > r || Math.Max(p0, Math.Max(p1, p2)) < -r;
        }
    }

    private static float Get(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
}
