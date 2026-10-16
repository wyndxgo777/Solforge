using SunriseStudio.Formats;
using SunriseStudio.Live;

namespace SunriseStudio.Editor;

/** How the game holds one widget of the open hierarchy now; Unknown is a node a partial tree did not reach. */
public enum CuiLiveState { NotBuilt, Shown, Hidden, HiddenByParent, Unknown }

/** Pairs the game's widget rows with the open hierarchy's nodes by node id. */
public sealed class CuiLiveJoin
{
    private readonly Dictionary<int, LiveCuiWidget> rows;
    private readonly Dictionary<int, bool> shown;
    public static readonly CuiLiveJoin Empty = new([], [], 0, 0, 0, false);
    public int Matched { get; }
    /** Rows whose node is missing here or has another name; the game runs a different build or edit. */
    public int Mismatched { get; }
    /** Rows of a nested hierarchy; Studio does not place them. */
    public int OtherHierarchy { get; }
    /** The game cut its walk short, so a missing row does not mean the widget is not built. */
    public bool Partial { get; }

    private CuiLiveJoin(Dictionary<int, LiveCuiWidget> rows, Dictionary<int, bool> shown, int matched, int mismatched, int other,
        bool partial)
    { this.rows = rows; this.shown = shown; Matched = matched; Mismatched = mismatched; OtherHierarchy = other; Partial = partial; }

    /** The game's widget row for the node id, or null when the game has none. */
    public LiveCuiWidget? Row(int node) => rows.GetValueOrDefault(node);

    /** How the game currently holds the node (not built, shown, hidden, hidden by a parent or unknown). */
    public CuiLiveState State(int node) =>
        !rows.TryGetValue(node, out var row) ? Partial ? CuiLiveState.Unknown : CuiLiveState.NotBuilt
            : shown[node] ? CuiLiveState.Shown : row.Visible ? CuiLiveState.HiddenByParent : CuiLiveState.Hidden;

    /** True unless the game built the widget and hides it, itself or through a parent. */
    public bool ShownOrUnknown(int node) => shown.GetValueOrDefault(node, true);

    /**
     * Joins one tree answer to the open hierarchy's nodes.
     * @param nodes The composed nodes of the hierarchy.
     * @param hierarchyTag The open hierarchy; rows with another tag are counted, never joined.
     * @param tree The game's rows; a parent row always precedes its children.
     */
    public static CuiLiveJoin Build(IReadOnlyList<CuiComposedNode> nodes, uint hierarchyTag, LiveCuiTree? tree)
    {
        if (tree is not { Status: LiveCuiStatus.Complete } || tree.Rows.Count == 0) return Empty;
        var byId = new Dictionary<int, CuiComposedNode>(nodes.Count);
        foreach (var node in nodes) byId.TryAdd(node.Id, node);
        var rowShown = new bool[tree.Rows.Count];
        Dictionary<int, LiveCuiWidget> joined = [];
        Dictionary<int, bool> shown = [];
        int matched = 0, mismatched = 0, other = 0;
        for (int i = 0; i < tree.Rows.Count; i++)
        {
            var row = tree.Rows[i];
            // Visible is true for a row that is not a widget, so such a row never hides its children.
            rowShown[i] = row.Visible && (row.ParentRow is not { } parent || parent < i && rowShown[parent]);
            if (row.HierarchyTag != hierarchyTag) { other++; continue; }
            // A node with no authored name, like the screen root, joins on its id alone.
            if (!byId.TryGetValue(row.NodeId, out var node)
                || node.Definition.NameHash != 0 && node.Definition.NameHash != row.NameHash) { mismatched++; continue; }
            if (!joined.TryAdd(row.NodeId, row)) continue;
            shown[row.NodeId] = rowShown[i];
            matched++;
        }
        return new(joined, shown, matched, mismatched, other, tree.Truncated || tree.WalkFaulted);
    }
}
