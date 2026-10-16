using System.Numerics;

namespace SunriseStudio.Viewport;

/** XY grid over bounding spheres for frustum and screen-size culling. */
public sealed class SceneGrid
{
    private sealed class Cell
    {
        public Vector3 Min = new(float.PositiveInfinity), Max = new(float.NegativeInfinity);
        public float MaxRadius;
        public List<int> Items = [];
        public int Index;
    }

    // Cells per axis; bounds the per-frame cell loop.
    private const int MaxCellsPerAxis = 96;
    private readonly List<Cell> cells = [];
    private readonly Vector3[] centers;
    private readonly float[] radii;
    // Each item's cell index; -1 for an item no cell holds.
    private readonly int[] cellOf;
    private readonly List<int> candidates = [];
    private Vector4[] candidatePlanes = [];
    private Vector3 candidateEye;
    private float candidatePixelsPerUnit, candidateMinimumPixels;
    private bool candidateOrthographic, candidatesReady;
    public int CandidateBuildCount { get; private set; }

    public SceneGrid(Vector3[] centers, float[] radii)
    {
        if (centers.Length != radii.Length) throw new ArgumentException("Sphere centers and radii differ in count.");
        this.centers = centers = centers.ToArray();
        this.radii = radii = radii.ToArray();
        cellOf = new int[centers.Length];
        Array.Fill(cellOf, -1);
        if (centers.Length == 0) return;
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        for (int i = 0; i < centers.Length; i++)
            if (Finite(centers[i]) && float.IsFinite(radii[i]))
            { min = Vector3.Min(min, centers[i]); max = Vector3.Max(max, centers[i]); }
        if (!Finite(min)) return;
        float cellSize = MathF.Max(MathF.Max(max.X - min.X, max.Y - min.Y) / MaxCellsPerAxis, 8);
        int columns = Math.Max(1, (int)((max.X - min.X) / cellSize) + 1);
        var byKey = new Dictionary<int, Cell>();
        // Spheres wider than a cell each get their own cell so they never hold a small cell open.
        for (int i = 0; i < centers.Length; i++)
        {
            if (!Finite(centers[i]) || !float.IsFinite(radii[i])) continue;
            Cell cell;
            if (radii[i] > cellSize) cells.Add(cell = new Cell { Index = cells.Count });
            else
            {
                int x = (int)((centers[i].X - min.X) / cellSize), y = (int)((centers[i].Y - min.Y) / cellSize);
                int key = y * columns + x;
                if (!byKey.TryGetValue(key, out cell!)) { byKey.Add(key, cell = new Cell { Index = cells.Count }); cells.Add(cell); }
            }
            cell.Items.Add(i);
            cellOf[i] = cell.Index;
            cell.Min = Vector3.Min(cell.Min, centers[i] - new Vector3(radii[i]));
            cell.Max = Vector3.Max(cell.Max, centers[i] + new Vector3(radii[i]));
            cell.MaxRadius = MathF.Max(cell.MaxRadius, radii[i]);
        }
        // Largest first, so a cell stops at its first item too small to see.
        foreach (var cell in cells) cell.Items.Sort((a, b) => radii[b].CompareTo(radii[a]));
    }

    /**
     * Collects items inside the frustum whose projected radius reaches minPixels.
     * @param pixelsPerUnit Projected pixels for one unit at distance one; orthographic when ortho is set.
     */
    public void Query(Vector4[] planes, Vector3 eye, float pixelsPerUnit, bool ortho, float minPixels,
        Func<int, bool> shown, List<int> output) =>
        QueryCore(planes, eye, pixelsPerUnit, ortho, minPixels, shown, output, null);

    /** Exact spatial reuse never caches a visibility predicate's result or changes traversal order. */
    public void QueryCached(Vector4[] planes, Vector3 eye, float pixelsPerUnit, bool ortho, float minPixels,
        Func<int, bool> shown, List<int> output)
    {
        if (candidatesReady && candidatePlanes.AsSpan().SequenceEqual(planes)
            && candidateEye == eye && candidatePixelsPerUnit == pixelsPerUnit
            && candidateOrthographic == ortho && candidateMinimumPixels == minPixels)
        {
            foreach (int index in candidates)
                if (shown(index)) output.Add(index);
            return;
        }
        candidatesReady = false;
        candidates.Clear();
        QueryCore(planes, eye, pixelsPerUnit, ortho, minPixels, shown, output, candidates);
        candidatePlanes = planes.ToArray();
        candidateEye = eye; candidatePixelsPerUnit = pixelsPerUnit;
        candidateOrthographic = ortho; candidateMinimumPixels = minPixels;
        CandidateBuildCount++;
        candidatesReady = true;
    }

    /** A cold query records spatial candidates before applying the current visibility policy. */
    private void QueryCore(Vector4[] planes, Vector3 eye, float pixelsPerUnit, bool ortho, float minPixels,
        Func<int, bool> shown, List<int> output, List<int>? spatialCandidates)
    {
        foreach (var cell in cells)
        {
            var test = BoxInFrustum(cell.Min, cell.Max, planes);
            if (test == Containment.Outside) continue;
            float near = ortho ? 1 : MathF.Max(1e-3f, Vector3.Distance(eye, Vector3.Clamp(eye, cell.Min, cell.Max)));
            if (minPixels > 0 && cell.MaxRadius * pixelsPerUnit / near < minPixels) continue;
            foreach (int i in cell.Items)
            {
                float r = radii[i];
                if (minPixels > 0 && r * pixelsPerUnit / near < minPixels) break;
                var c = centers[i];
                if (test == Containment.Partial && !SphereInFrustum(c, r, planes)) continue;
                if (minPixels > 0)
                {
                    float d = ortho ? 1 : MathF.Max(1e-3f, Vector3.Distance(eye, c) - r);
                    if (r * pixelsPerUnit / d < minPixels) continue;
                }
                spatialCandidates?.Add(i);
                if (shown(i)) output.Add(i);
            }
        }
    }

    /** The items one fixed visibility policy shows, per cell, in query order. */
    public sealed class ShownCells
    {
        // Items, Spheres, Codes and Kept may be longer than the Starts[^1] items they hold.
        internal int[] Starts = [], Items = [];
        // Each Items entry's sphere, centre and radius, so the query reads them in order.
        internal Vector4[] Spheres = [];
        // Each Items entry's group code; empty when PrepareShown had no groups.
        internal int[] Codes = [];
        // The per-item group codes Codes came from; an incremental PrepareShown reads them again.
        internal int[]? ItemGroups;
        // Query scratch: each cell's containment, the ranges it filters and the survivors at each range's own start.
        internal Containment[] Tests = [];
        internal int[] Kept = [];
        internal readonly List<(int Start, int End, bool Partial)> Runs = [];
        internal int[] RunKept = [];
        // Query scratch: the output in order, as Items ranges and as Runs (Run >= 0).
        internal readonly List<(int Start, int End, int Run)> Segments = [];
        internal int Count => Starts[^1];
    }

    // The previous argument of the last incremental PrepareShown; the next call reuses its arrays.
    private ShownCells? spare;
    // Incremental PrepareShown scratch, all false between calls.
    private bool[] markedScratch = [], dirtyScratch = [];
    private readonly List<int> changedScratch = [];

    // Below this many shown items the query tests cells on the calling thread.
    private const int MinimumParallelItems = 32768;
    // Cells per parallel task.
    private const int CellsPerTask = 64;
    // Items per filtered query range, so a large cell spreads over several threads.
    private const int RunItems = 4096;

    /**
     * Runs shown once per item; the result stays valid while the policy and this grid stay the same.
     * @param shown Runs on several threads for a large grid, so it may only read state.
     * @param groups Each item's group code for QueryShown's masks, or -1 for none. Read, not copied.
     */
    public ShownCells PrepareShown(Func<int, bool> shown, int[]? groups = null)
    {
        var starts = new int[cells.Count + 1];
        // Each cell first fills from its own offset, so cells can run in parallel.
        var offsets = new int[cells.Count + 1];
        for (int c = 0; c < cells.Count; c++) offsets[c + 1] = offsets[c] + cells[c].Items.Count;
        var items = new int[offsets[cells.Count]];
        var counts = new int[cells.Count];
        void Fill(int from, int to)
        {
            for (int c = from; c < to; c++)
            {
                int next = offsets[c];
                foreach (int i in cells[c].Items) if (shown(i)) items[next++] = i;
                counts[c] = next - offsets[c];
            }
        }
        if (items.Length < MinimumParallelItems) Fill(0, cells.Count);
        else Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, cells.Count, CellsPerTask),
            range => Fill(range.Item1, range.Item2));
        // Close the gaps in cell order; a run only moves toward the start, so it never overwrites a later one.
        int total = 0;
        for (int c = 0; c < cells.Count; c++)
        {
            starts[c] = total;
            Array.Copy(items, offsets[c], items, total, counts[c]);
            total += counts[c];
        }
        starts[cells.Count] = total;
        Array.Resize(ref items, total);
        var result = new ShownCells
        {
            Starts = starts, Items = items, Tests = new Containment[cells.Count], Kept = new int[total],
            Spheres = new Vector4[total], Codes = groups is null ? [] : new int[total], ItemGroups = groups
        };
        void Describe(int from, int to)
        {
            for (int k = from; k < to; k++)
            {
                int i = items[k];
                result.Spheres[k] = new(centers[i], radii[i]);
                if (groups is not null) result.Codes[k] = groups[i];
            }
        }
        if (total < MinimumParallelItems) Describe(0, total);
        else Parallel.For(0, (total + RunItems - 1) / RunItems, run => Describe(run * RunItems, Math.Min(total, (run + 1) * RunItems)));
        return result;
    }

    /**
     * PrepareShown for a policy that answers as previous's did for every item but the changed ones.
     * Only the changed items run shown again; every other item keeps its previous answer.
     * @param previous Handed over: the call after next reuses its arrays, so the caller drops it.
     */
    public ShownCells PrepareShown(Func<int, bool> shown, ShownCells previous, IEnumerable<int> changed)
    {
        if (previous.Starts.Length != cells.Count + 1) throw new ArgumentException("Shown cells belong to another grid.", nameof(previous));
        if (markedScratch.Length != cellOf.Length) markedScratch = new bool[cellOf.Length];
        if (dirtyScratch.Length != cells.Count) dirtyScratch = new bool[cells.Count];
        var marked = markedScratch; var dirty = dirtyScratch;
        changedScratch.Clear();
        foreach (int i in changed)
            if (cellOf[i] >= 0) { dirty[cellOf[i]] = true; marked[i] = true; changedScratch.Add(i); }
        var result = spare is { } reused && !ReferenceEquals(reused, previous) && reused.Starts.Length == cells.Count + 1 ? reused
            : new ShownCells { Starts = new int[cells.Count + 1], Tests = new Containment[cells.Count] };
        // Only a changed item can join, so the previous count plus the changed count bounds the result.
        int capacity = previous.Count + changedScratch.Count;
        var groups = result.ItemGroups = previous.ItemGroups;
        if (result.Items.Length < capacity) { result.Items = new int[capacity]; result.Spheres = new Vector4[capacity]; }
        if (groups is null) result.Codes = [];
        else if (result.Codes.Length < capacity) result.Codes = new int[capacity];
        var starts = result.Starts; var items = result.Items; var spheres = result.Spheres; var codes = result.Codes;
        int total = 0;
        for (int c = 0; c < cells.Count; c++)
        {
            starts[c] = total;
            int k = previous.Starts[c], end = previous.Starts[c + 1];
            if (!dirty[c])
            {
                Array.Copy(previous.Items, k, items, total, end - k);
                Array.Copy(previous.Spheres, k, spheres, total, end - k);
                if (groups is not null) Array.Copy(previous.Codes, k, codes, total, end - k);
                total += end - k;
                continue;
            }
            // The previous run keeps the cell's item order, so one pass reads each item's previous answer.
            foreach (int i in cells[c].Items)
            {
                bool was = k < end && previous.Items[k] == i;
                if (was) k++;
                if (!(marked[i] ? shown(i) : was)) continue;
                spheres[total] = new(centers[i], radii[i]);
                if (groups is not null) codes[total] = groups[i];
                items[total++] = i;
            }
        }
        starts[cells.Count] = total;
        if (result.Kept.Length < total) result.Kept = new int[total];
        foreach (int i in changedScratch) { marked[i] = false; dirty[cellOf[i]] = false; }
        spare = previous;
        return result;
    }

    /**
     * Matches Query with minPixels zero and the policy of PrepareShown, in the same order.
     * A cell inside every plane skips its sphere tests; large cells split into ranges that run in parallel.
     * @param masks Drops an item whose group code g has (masks[g >> 5] & g & 31) zero; null keeps every item.
     *   A group code below zero never drops.
     */
    public void QueryShown(Vector4[] planes, ShownCells shown, List<int> output, byte[]? masks = null)
    {
        if (shown.Starts.Length != cells.Count + 1) throw new ArgumentException("Shown cells belong to another grid.", nameof(shown));
        if (shown.ItemGroups is null) masks = null;
        var runs = shown.Runs; var segments = shown.Segments;
        runs.Clear(); segments.Clear();
        int work = 0;
        for (int c = 0; c < cells.Count; c++)
        {
            int start = shown.Starts[c], end = shown.Starts[c + 1];
            if (start == end) { shown.Tests[c] = Containment.Outside; continue; }
            var cell = cells[c];
            var test = shown.Tests[c] = BoxInFrustum(cell.Min, cell.Max, planes);
            if (test == Containment.Outside) continue;
            if (test == Containment.Inside && masks is null) { segments.Add((start, end, -1)); continue; }
            for (int from = start; from < end; from += RunItems)
            {
                segments.Add((0, 0, runs.Count));
                runs.Add((from, Math.Min(end, from + RunItems), test == Containment.Partial));
            }
            work += end - start;
        }
        if (shown.RunKept.Length < runs.Count) shown.RunKept = new int[Math.Max(runs.Count, shown.RunKept.Length * 2)];
        if (work < MinimumParallelItems) for (int r = 0; r < runs.Count; r++) FilterRun(planes, shown, masks, r);
        else Parallel.For(0, runs.Count, r => FilterRun(planes, shown, masks, r));
        foreach (var (start, end, run) in segments)
            if (run < 0) output.AddRange(new ReadOnlySpan<int>(shown.Items, start, end - start));
            else output.AddRange(new ReadOnlySpan<int>(shown.Kept, shown.Runs[run].Start, shown.RunKept[run]));
    }

    /** Writes run r's survivors, in order, at the run's own start in Kept. */
    private static void FilterRun(Vector4[] planes, ShownCells shown, byte[]? masks, int r)
    {
        var (start, end, partial) = shown.Runs[r];
        var items = shown.Items; var spheres = shown.Spheres; var codes = shown.Codes; var kept = shown.Kept;
        int count = 0;
        for (int k = start; k < end; k++)
        {
            if (masks is not null && codes[k] is >= 0 and var code && (masks[code >> 5] & code & 31) == 0) continue;
            if (partial && !SphereInFrustum(spheres[k], planes)) continue;
            kept[start + count++] = items[k];
        }
        shown.RunKept[r] = count;
    }

    private static bool SphereInFrustum(Vector4 sphere, Vector4[] planes)
    {
        foreach (var p in planes)
            if (p.X * sphere.X + p.Y * sphere.Y + p.Z * sphere.Z + p.W < -sphere.W) return false;
        return true;
    }

    /** Where a box lies against the frustum. */
    internal enum Containment : byte { Outside, Partial, Inside }

    /** Planes use Ax + By + Cz + D >= 0 for the inside. */
    public static Vector4[] FrustumPlanes(Matrix4x4 m)
    {
        Vector4[] planes =
        [
            new(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41),
            new(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41),
            new(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42),
            new(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42),
            new(m.M13, m.M23, m.M33, m.M43),
            new(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43)
        ];
        for (int i = 0; i < planes.Length; i++)
        {
            float length = new Vector3(planes[i].X, planes[i].Y, planes[i].Z).Length();
            if (length > 1e-8f) planes[i] /= length;
        }
        return planes;
    }

    /** True when a sphere touches the frustum. */
    public static bool SphereInFrustum(Vector3 c, float r, Vector4[] planes)
    {
        foreach (var p in planes)
            if (p.X * c.X + p.Y * c.Y + p.Z * c.Z + p.W < -r) return false;
        return true;
    }

    private static Containment BoxInFrustum(Vector3 min, Vector3 max, Vector4[] planes)
    {
        var result = Containment.Inside;
        foreach (var p in planes)
        {
            float x = p.X >= 0 ? max.X : min.X, y = p.Y >= 0 ? max.Y : min.Y, z = p.Z >= 0 ? max.Z : min.Z;
            if (p.X * x + p.Y * y + p.Z * z + p.W < 0) return Containment.Outside;
            x = p.X >= 0 ? min.X : max.X; y = p.Y >= 0 ? min.Y : max.Y; z = p.Z >= 0 ? min.Z : max.Z;
            if (p.X * x + p.Y * y + p.Z * z + p.W < 0) result = Containment.Partial;
        }
        return result;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
