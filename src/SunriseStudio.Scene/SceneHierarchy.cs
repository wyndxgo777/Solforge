namespace SunriseStudio.Scene;

/** Kind of hierarchy node. */
public enum SceneHierarchyKind
{
    Root, Bubble, Map, Mission, State, Container, SharedContainers,
    AuthoredObject, Placement, Marker, Light, Squad
}

/** Members share one decoded ownership identity; children do not imply native component links. */
public sealed class SceneHierarchyNode
{
    internal readonly List<SceneHierarchyNode> ChildList = [];
    internal readonly List<object> MemberList = [];
    internal int First, End;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SceneHierarchyKind Kind { get; init; }
    public SceneHierarchyNode? Parent { get; internal set; }
    public int Depth { get; internal set; }
    public IReadOnlyList<SceneHierarchyNode> Children => ChildList;
    public IReadOnlyList<object> Members => MemberList;
    public WorldObject? Representative { get; internal set; }
    public IReadOnlyList<uint> ContainerTags { get; internal set; } = [];
    public bool Visible { get; internal set; } = true;
}

/** Visibility changes update subtree flags once; render predicates read one source lookup. */
public sealed partial class SceneHierarchy
{
    private readonly Dictionary<string, SceneHierarchyNode> byId = new(StringComparer.Ordinal);
    private readonly Dictionary<object, SceneHierarchyNode> bySource = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, SceneHierarchyNode> byObjectId = new(StringComparer.Ordinal);
    private readonly HashSet<string> hidden = new(StringComparer.Ordinal);
    private SceneHierarchyNode[] ordered = [];
    public SceneHierarchyNode Root { get; private set; } = null!;
    public IReadOnlyList<SceneHierarchyNode> Nodes => ordered;
    public IReadOnlySet<string> HiddenNodeIds => hidden;
    public long Revision { get; private set; }

    /** Hide IDs may be carried across reloads of the same world or asset. */
    public static SceneHierarchy Build(WorldDocument world, IEnumerable<string>? hiddenIds = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        var result = new SceneHierarchy();
        if (hiddenIds is not null) result.hidden.UnionWith(hiddenIds);
        result.BuildNodes(world);
        return result;
    }

    /** Finds a node by id or by the id of its source object. */
    public SceneHierarchyNode? Find(string id) => byId.GetValueOrDefault(id)
        ?? byObjectId.GetValueOrDefault(id);

    /** Viewport proxies resolve through their source key, including metadata without meshes. */
    public SceneHierarchyNode? Find(object? source)
    {
        if (source is null) return null;
        if (source is ISceneSelectionProxy proxy) source = proxy.Key;
        if (source is SceneHierarchyNode node) return byId.GetValueOrDefault(node.Id);
        if (source is WorldObject row) return byObjectId.GetValueOrDefault(row.Id);
        return source is string id ? Find(id) : bySource.GetValueOrDefault(source);
    }

    /** A moved object keeps its node; only the node's row is replaced. */
    public void ReplaceRows(IEnumerable<WorldObject> rows)
    {
        foreach (var row in rows)
            if (byObjectId.TryGetValue(row.Id, out var node) && node.Representative?.Id == row.Id) node.Representative = row;
    }

    /** False only when the object's node is hidden; objects without a node count as visible. */
    public bool IsVisible(object source) => Find(source)?.Visible ?? true;
    /** True when the node with this id was hidden directly. */
    public bool IsHidden(string id) => hidden.Contains(id);
    /** True when the node is hidden itself or through an ancestor. */
    public bool IsEffectivelyHidden(SceneHierarchyNode node) => !node.Visible;

    /** Clearing the view also removes unmatched hide IDs retained for undo. */
    public void ShowAll()
    {
        if (hidden.Count == 0) return;
        hidden.Clear();
        foreach (var node in ordered) node.Visible = true;
        Revision++;
    }

    /** Child hide choices survive hiding and showing an ancestor. */
    public bool SetHidden(string id, bool value)
    {
        if (!byId.TryGetValue(id, out var node)) return false;
        if (!(value ? hidden.Add(id) : hidden.Remove(id))) return false;
        for (int i = node.First; i < node.End; i++)
        {
            var current = ordered[i];
            current.Visible = (current.Parent?.Visible ?? true) && !hidden.Contains(current.Id);
        }
        Revision++;
        return true;
    }

    /** Revealing a selection clears only its own and ancestor hide flags. */
    public bool Reveal(SceneHierarchyNode node)
    {
        bool changed = false;
        foreach (var ancestor in PathTo(node)) changed |= SetHidden(ancestor.Id, false);
        return changed;
    }

    /** The chain of nodes from the root to this node. */
    public IReadOnlyList<SceneHierarchyNode> PathTo(SceneHierarchyNode node)
    {
        var path = new List<SceneHierarchyNode>();
        for (var current = node; current is not null; current = current.Parent) path.Add(current);
        path.Reverse();
        return path;
    }

    /** Descendant enumeration is for user actions, not per-frame render filtering. */
    public IEnumerable<object> DescendantMembers(SceneHierarchyNode node)
    {
        for (int i = node.First; i < node.End; i++)
            foreach (var member in ordered[i].Members) yield return member;
    }

    /** Labels never enter branch identity, so renaming cannot change saved hide choices. */
    private SceneHierarchyNode Add(string id, string name, SceneHierarchyKind kind,
        SceneHierarchyNode? parent)
    {
        if (byId.TryGetValue(id, out var existing)) return existing;
        var node = new SceneHierarchyNode
        { Id = id, Name = name, Kind = kind, Parent = parent, Depth = parent is null ? 0 : parent.Depth + 1 };
        byId.Add(id, node);
        parent?.ChildList.Add(node);
        return node;
    }

    /** A source has one exclusive tree owner even when its world row has several references. */
    private void Associate(SceneHierarchyNode node, WorldObject row)
    {
        if (bySource.TryAdd(row.Source, node)) node.MemberList.Add(row.Source);
        byObjectId.TryAdd(row.Id, node);
        node.Representative ??= row;
    }

    /** Depth-first ranges make branch changes linear in only the affected subtree. */
    private void Finish()
    {
        var nodes = new List<SceneHierarchyNode>(byId.Count);
        void Visit(SceneHierarchyNode node)
        {
            node.First = nodes.Count;
            nodes.Add(node);
            node.Visible = (node.Parent?.Visible ?? true) && !hidden.Contains(node.Id);
            node.ChildList.Sort((a, b) =>
            {
                int kind = a.Kind.CompareTo(b.Kind);
                int name = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
                return kind != 0 ? kind : name != 0 ? name : StringComparer.Ordinal.Compare(a.Id, b.Id);
            });
            foreach (var child in node.Children) Visit(child);
            node.End = nodes.Count;
        }
        Visit(Root);
        ordered = nodes.ToArray();
    }
}
