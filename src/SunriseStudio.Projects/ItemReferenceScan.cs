using System.Buffers.Binary;
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

/** One tag that references an item: Via is the root (activity, project tag) whose walk reached it; Hash is true when it names the definition hash. */
public sealed record ItemReferenceHit(uint Tag, uint Via, bool Hash, int Offset);

/** What a bounded reference walk found and whether it stopped early. */
public sealed record ItemReferenceScanResult(IReadOnlyList<ItemReferenceHit> Hits, int Visited, bool Truncated);

/**
 * T1.4 get_item_sources: which tags reach an item. Walks TagDependencyGraph from roots (activities, the project's
 * edited tags from DependencyManifest) and reports every visited tag that references one of the item's tags (definition
 * or display) or holds its 32-bit definition hash, with the root it was reached from. The walk is bounded by node count,
 * so a whole-install scan stays affordable; Truncated says the bound cut it short.
 */
public static class ItemReferenceScan
{
    /** TagDependencyGraph checks every new edge for a cycle, so the walk stays in the low thousands of tags. */
    public const int DefaultMaximumNodes = 8000;

    /**
     * @param references Tag references of a tag (the package dependency words); returns nothing for leaf tags.
     * @param read Tag bytes for the hash search; null skips it for that tag.
     */
    public static ItemReferenceScanResult Scan(IReadOnlyList<uint> roots, Func<uint, IEnumerable<uint>> references,
        Func<uint, byte[]?> read, IReadOnlySet<uint> itemTags, uint itemHash, int maximumNodes = DefaultMaximumNodes,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(itemTags);
        // Item tags are leaves here: walking into the item itself would only find what the item references.
        var graph = TagDependencyGraph.Build(roots, tag => itemTags.Contains(tag) ? Enumerable.Empty<uint>() : references(tag), maximumNodes, token);
        var hits = new Dictionary<uint, ItemReferenceHit>();
        foreach (uint item in itemTags.Where(tag => graph.Nodes.Contains(tag)))
            foreach (uint parent in graph.DependentsOf(item))
                hits.TryAdd(parent, new(parent, 0, false, -1));
        if (itemHash != 0)
            foreach (uint tag in graph.Nodes)
            {
                token.ThrowIfCancellationRequested();
                if (hits.ContainsKey(tag) || itemTags.Contains(tag) || read(tag) is not { } data) continue;
                for (int offset = 0; offset + 4 <= data.Length; offset += 4)
                    if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) == itemHash)
                    {
                        hits[tag] = new(tag, 0, true, offset);
                        break;
                    }
            }
        // Name the root each hit came from: walk dependents upward to the nearest root.
        var rootSet = roots.ToHashSet();
        var result = hits.Values.OrderBy(hit => hit.Tag).Select(hit => hit with { Via = RootOf(graph, rootSet, hit.Tag) }).ToArray();
        return new(result, graph.NodeCount, graph.NodeCount >= maximumNodes);
    }

    /** The nearest root above tag (breadth first over dependents), or 0. */
    private static uint RootOf(TagDependencyGraph graph, HashSet<uint> roots, uint tag)
    {
        var seen = new HashSet<uint> { tag };
        var queue = new Queue<uint>();
        queue.Enqueue(tag);
        while (queue.TryDequeue(out uint next))
        {
            if (roots.Contains(next)) return next;
            foreach (uint parent in graph.DependentsOf(next).Order())
                if (seen.Add(parent)) queue.Enqueue(parent);
        }
        return 0;
    }

    /** Roots from the project's dependency manifest whose own references include one of the item's tags. */
    public static IReadOnlyList<DependencyNode> ProjectReferences(DependencyManifest manifest, IReadOnlySet<uint> itemTags)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Nodes.Where(node => !itemTags.Contains(node.Tag) && node.References.Any(itemTags.Contains)).ToArray();
    }
}
