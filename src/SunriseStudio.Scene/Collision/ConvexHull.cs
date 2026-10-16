using System.Numerics;

namespace SunriseStudio.Scene.Collision;

/** Convex hull of a small point set, in double precision. */
public static class ConvexHull
{
    // Points closer than this fraction of the set's size to a plane count as on it.
    private const double RelativeTolerance = 1e-6;

    /**
     * Hull triangles as indices into the points, wound counterclockwise seen from outside.
     * @return One fan for a flat set, and nothing for a set with no area.
     */
    public static int[] Triangles(IReadOnlyList<Vector3> points)
    {
        var p = points.Select(v => new D3(v.X, v.Y, v.Z)).ToArray();
        if (p.Length < 3) return [];
        var min = p.Aggregate(D3.Min);
        var max = p.Aggregate(D3.Max);
        double eps = RelativeTolerance * Math.Max((max - min).Length, 1e-9);
        if ((max - min).Length <= eps) return [];

        int a = 0;
        for (int i = 1; i < p.Length; i++) if (p[i].X < p[a].X) a = i;
        int b = ArgMax(p.Length, i => (p[i] - p[a]).Length);
        if ((p[b] - p[a]).Length <= eps) return [];
        var ab = (p[b] - p[a]).Normalized;
        int c = ArgMax(p.Length, i => D3.Cross(ab, p[i] - p[a]).Length);
        var normal = D3.Cross(p[b] - p[a], p[c] - p[a]);
        if (normal.Length <= eps * (p[b] - p[a]).Length) return [];
        normal = normal.Normalized;
        int d = ArgMax(p.Length, i => Math.Abs(D3.Dot(normal, p[i] - p[a])));
        if (Math.Abs(D3.Dot(normal, p[d] - p[a])) <= eps) return Flat(p, a, b, normal);

        var faces = new List<Face>();
        var edges = new Dictionary<(int, int), int>();
        var center = (p[a] + p[b] + p[c] + p[d]) * 0.25;
        foreach (var (x, y, z) in new[] { (a, b, c), (a, c, d), (a, d, b), (b, d, c) }) AddFace(x, y, z, center);
        var used = new HashSet<int> { a, b, c, d };
        // Farthest point first keeps new faces well shaped.
        while (true)
        {
            int best = -1, bestFace = -1;
            double bestDistance = eps;
            for (int i = 0; i < p.Length; i++)
            {
                if (used.Contains(i)) continue;
                for (int f = 0; f < faces.Count; f++)
                {
                    if (!faces[f].Alive) continue;
                    double distance = faces[f].Distance(p[i]);
                    if (distance > bestDistance) (best, bestFace, bestDistance) = (i, f, distance);
                }
            }
            if (best < 0) break;
            used.Add(best);
            var visible = new HashSet<int>();
            var pending = new Stack<int>([bestFace]);
            while (pending.Count > 0)
            {
                int f = pending.Pop();
                if (!visible.Add(f)) continue;
                foreach (var (u, v) in faces[f].Edges())
                    if (edges.TryGetValue((v, u), out int g) && faces[g].Alive && !visible.Contains(g) && faces[g].Distance(p[best]) > eps)
                        pending.Push(g);
            }
            var horizon = new List<(int, int)>();
            foreach (int f in visible)
                foreach (var (u, v) in faces[f].Edges())
                    if (!edges.TryGetValue((v, u), out int g) || !visible.Contains(g)) horizon.Add((u, v));
            foreach (int f in visible)
            {
                faces[f] = faces[f] with { Alive = false };
                foreach (var edge in faces[f].Edges()) edges.Remove(edge);
            }
            foreach (var (u, v) in horizon) AddFace(u, v, best, null);
        }
        return faces.Where(f => f.Alive).SelectMany(f => new[] { f.A, f.B, f.C }).ToArray();

        void AddFace(int x, int y, int z, D3? inside)
        {
            var n = D3.Cross(p[y] - p[x], p[z] - p[x]);
            if (inside is { } point && D3.Dot(n, point - p[x]) > 0) (y, z, n) = (z, y, -n);
            n = n.Length > 0 ? n.Normalized : n;
            faces.Add(new Face(x, y, z, n, D3.Dot(n, p[x]), true));
            foreach (var edge in faces[^1].Edges()) edges[edge] = faces.Count - 1;
        }
    }

    /**
     * Outward face planes (normal, offset) of the hull, one per merged face. A flat set gives both
     * faces and one plane per boundary edge. Every point has dot(normal, point) + offset <= 0.
     * @param angle Largest angle in radians between triangle normals that still counts as one face.
     */
    public static Vector4[] Planes(IReadOnlyList<Vector3> points, double angle = 1e-3)
    {
        int[] triangles = Triangles(points);
        var p = points.Select(v => new D3(v.X, v.Y, v.Z)).ToArray();
        var normals = new List<D3>();
        double cos = Math.Cos(angle);
        for (int t = 0; t < triangles.Length; t += 3)
        {
            var n = D3.Cross(p[triangles[t + 1]] - p[triangles[t]], p[triangles[t + 2]] - p[triangles[t]]);
            if (n.Length == 0) continue;
            n = n.Normalized;
            int match = normals.FindIndex(m => D3.Dot(m, n) >= cos);
            if (match < 0) normals.Add(n);
        }
        // Installed flat shapes carry both faces and one plane per boundary edge.
        if (normals.Count == 1)
        {
            var face = normals[0];
            normals.Add(-face);
            var edges = new HashSet<(int, int)>();
            for (int t = 0; t < triangles.Length; t += 3)
                for (int k = 0; k < 3; k++) edges.Add((triangles[t + k], triangles[t + (k + 1) % 3]));
            foreach (var (a, b) in edges.Where(e => !edges.Contains((e.Item2, e.Item1))))
                normals.Add(D3.Cross(p[b] - p[a], face).Normalized);
        }
        return normals.Select(n =>
        {
            double offset = -p.Max(point => D3.Dot(n, point));
            return new Vector4((float)n.X, (float)n.Y, (float)n.Z, (float)offset);
        }).ToArray();
    }

    /** One fan over the 2D hull of a flat set, wound around the given normal. */
    private static int[] Flat(D3[] p, int a, int b, D3 normal)
    {
        var u = (p[b] - p[a]).Normalized;
        var w = D3.Cross(normal, u);
        var order = Enumerable.Range(0, p.Length).Select(i => (Index: i, X: D3.Dot(p[i] - p[a], u), Y: D3.Dot(p[i] - p[a], w)))
            .OrderBy(q => q.X).ThenBy(q => q.Y).ThenBy(q => q.Index).ToArray();
        // Monotone chain, counterclockwise around the normal.
        var hull = new List<(int Index, double X, double Y)>();
        foreach (var pass in new[] { order, order.Reverse().ToArray() })
        {
            int start = hull.Count;
            foreach (var q in pass)
            {
                while (hull.Count >= start + 2 && Cross2(hull[^2], hull[^1], q) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(q);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        var result = new List<int>();
        for (int i = 1; i + 1 < hull.Count; i++) result.AddRange([hull[0].Index, hull[i].Index, hull[i + 1].Index]);
        return result.ToArray();

        static double Cross2((int, double X, double Y) o, (int, double X, double Y) s, (int, double X, double Y) t) =>
            (s.X - o.X) * (t.Y - o.Y) - (s.Y - o.Y) * (t.X - o.X);
    }

    private static int ArgMax(int count, Func<int, double> value)
    {
        int best = 0;
        double top = double.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            double v = value(i);
            if (v > top) (best, top) = (i, v);
        }
        return best;
    }

    private readonly record struct Face(int A, int B, int C, D3 Normal, double Offset, bool Alive)
    {
        /** Signed distance from the face plane to the point. */
        public double Distance(D3 point) => D3.Dot(Normal, point) - Offset;
        /** The face's three edges as vertex index pairs. */
        public (int, int)[] Edges() => [(A, B), (B, C), (C, A)];
    }

    private readonly record struct D3(double X, double Y, double Z)
    {
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public D3 Normalized => this * (1 / Length);
        public static D3 operator -(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static D3 operator -(D3 a) => new(-a.X, -a.Y, -a.Z);
        public static D3 operator *(D3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        /** Dot product. */
        public static double Dot(D3 a, D3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        /** Cross product. */
        public static D3 Cross(D3 a, D3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        /** Component-wise minimum. */
        public static D3 Min(D3 a, D3 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        /** Component-wise maximum. */
        public static D3 Max(D3 a, D3 b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
    }
}
