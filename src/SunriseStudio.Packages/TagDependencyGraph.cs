namespace SunriseStudio.Packages;

/**
 * Solforge Phase 07: an in-memory tag dependency DAG. An edge From -> To means From references To.
 * An edge that would close a cycle (including a self reference) is rejected, so the graph always has
 * a topological order. Not thread-safe; build it on one thread, then share it read-only.
 */
public sealed class TagDependencyGraph
{
    private readonly Dictionary<uint, HashSet<uint>> dependencies = [];
    private readonly Dictionary<uint, HashSet<uint>> dependents = [];
    private readonly List<(uint From, uint To)> rejected = [];

    public int NodeCount => dependencies.Count;
    public int EdgeCount { get; private set; }
    public IReadOnlyCollection<uint> Nodes => dependencies.Keys;
    /** Edges TryAdd refused because they would have closed a cycle, in the order they were offered. */
    public IReadOnlyList<(uint From, uint To)> RejectedEdges => rejected;

    /** Adds a tag as a node if it is not there yet. */
    public void AddNode(uint tag)
    {
        if (dependencies.ContainsKey(tag)) return;
        dependencies.Add(tag, []);
        dependents.Add(tag, []);
    }

    /** Adds From -> To unless it closes a cycle; a repeated edge is accepted once. */
    public bool TryAdd(uint from, uint to)
    {
        if (from == to || Reaches(to, from))
        {
            rejected.Add((from, to));
            return false;
        }
        AddNode(from);
        AddNode(to);
        if (dependencies[from].Add(to))
        {
            dependents[to].Add(from);
            EdgeCount++;
        }
        return true;
    }

    /** Adds From -> To or throws when it would close a cycle. */
    public void Add(uint from, uint to)
    {
        if (!TryAdd(from, to))
            throw new InvalidOperationException($"Tag 0x{from:X8} -> 0x{to:X8} would create a dependency cycle.");
    }

    /** Tags the given tag depends on. */
    public IReadOnlyCollection<uint> DependenciesOf(uint tag) =>
        dependencies.TryGetValue(tag, out var set) ? set : [];

    /** Tags that depend on the given tag. */
    public IReadOnlyCollection<uint> DependentsOf(uint tag) =>
        dependents.TryGetValue(tag, out var set) ? set : [];

    /** True when a path of one or more edges, or the empty path, leads from From to To. */
    public bool Reaches(uint from, uint to)
    {
        if (from == to) return dependencies.ContainsKey(from);
        if (!dependencies.ContainsKey(from) || !dependencies.ContainsKey(to)) return false;
        var seen = new HashSet<uint> { from };
        var stack = new Stack<uint>();
        stack.Push(from);
        while (stack.Count != 0)
            foreach (uint next in dependencies[stack.Pop()])
            {
                if (next == to) return true;
                if (seen.Add(next)) stack.Push(next);
            }
        return false;
    }

    /** Every tag reachable from root, root first, breadth first. */
    public IReadOnlyList<uint> Closure(uint root)
    {
        if (!dependencies.ContainsKey(root)) return [];
        var order = new List<uint> { root };
        var seen = new HashSet<uint> { root };
        for (int i = 0; i < order.Count; i++)
            foreach (uint next in dependencies[order[i]].Order())
                if (seen.Add(next)) order.Add(next);
        return order;
    }

    /** Dependencies before dependents (Kahn's algorithm, smallest hash first among ready tags). */
    public IReadOnlyList<uint> TopologicalOrder()
    {
        var remaining = dependencies.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
        var ready = new PriorityQueue<uint, uint>();
        foreach (var (tag, count) in remaining)
            if (count == 0) ready.Enqueue(tag, tag);
        var order = new List<uint>(remaining.Count);
        while (ready.TryDequeue(out uint tag, out _))
        {
            order.Add(tag);
            foreach (uint dependent in dependents[tag])
                if (--remaining[dependent] == 0) ready.Enqueue(dependent, dependent);
        }
        if (order.Count != remaining.Count)
            throw new InvalidOperationException("The tag dependency graph contains a cycle.");
        return order;
    }

    /**
     * Walks references breadth first from roots. Edges that would close a cycle land in RejectedEdges;
     * the walk stops adding new tags at maximumNodes, so a whole-install walk stays bounded.
     */
    public static TagDependencyGraph Build(IEnumerable<uint> roots, Func<uint, IEnumerable<uint>> references,
        int maximumNodes = int.MaxValue, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(references);
        var graph = new TagDependencyGraph();
        var queue = new Queue<uint>();
        foreach (uint root in roots)
        {
            if (graph.NodeCount >= maximumNodes) break;
            if (graph.dependencies.ContainsKey(root)) continue;
            graph.AddNode(root);
            queue.Enqueue(root);
        }
        var expanded = new HashSet<uint>();
        while (queue.TryDequeue(out uint tag))
        {
            token.ThrowIfCancellationRequested();
            if (!expanded.Add(tag)) continue;
            foreach (uint target in references(tag))
            {
                bool known = graph.dependencies.ContainsKey(target);
                if (!known && graph.NodeCount >= maximumNodes) continue;
                if (graph.TryAdd(tag, target) && !known) queue.Enqueue(target);
            }
        }
        return graph;
    }
}
