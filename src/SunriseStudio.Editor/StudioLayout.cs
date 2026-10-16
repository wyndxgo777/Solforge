using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SunriseStudio.Editor;

/**
 * Area editors. MissionGraph and NodeGraph are the old ids of the unified Graph panel: saved layouts, popouts and
 * callers that name them get Graph (Canonical); new layouts store Graph. Values only ever append.
 */
public enum StudioPanelKind { Empty, Workspace, Browser, Inspector, Changes, ObjectTree, MissionGraph, AI, NodeGraph, LocalServer, Graph }
/** Direction a split panel divides its children. */
public enum StudioSplitAxis { Columns, Rows }

/** One node of the panel layout tree: a leaf holding a panel kind, or a split with two children. */
public sealed class StudioLayoutNode
{
    public int Id { get; set; }
    public StudioPanelKind Panel { get; set; }
    public StudioSplitAxis Axis { get; set; }
    public float Ratio { get; set; } = .5f;
    public StudioLayoutNode? First { get; set; }
    public StudioLayoutNode? Second { get; set; }
    /** Side panels only: true collapsed to a strip, false always open, null collapsed while the panel has nothing to show. */
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Collapsed { get; set; }
    [JsonIgnore] public bool IsLeaf => First is null && Second is null;

    /** Panels that fold to a strip; the editor, the AI workspace and empty areas never do. */
    public static bool Collapsible(StudioPanelKind kind) => kind is StudioPanelKind.Browser or StudioPanelKind.Graph
        or StudioPanelKind.MissionGraph or StudioPanelKind.NodeGraph or StudioPanelKind.Changes or StudioPanelKind.ObjectTree or StudioPanelKind.Inspector;
}

/** The dockable panel layout: a bounded tree of panels that persists as JSON and splits, closes and shows panels. */
public sealed partial class StudioLayout
{
    public const int MaximumPanels = 16;
    private static readonly JsonSerializerOptions Json = new()
    { Converters = { new JsonStringEnumConverter() }, MaxDepth = 40 };
    public StudioLayoutNode Root { get; private set; }
    private int nextId;
    public IReadOnlyList<StudioLayoutNode> Panels => Nodes(Root).Where(node => node.IsLeaf).ToArray();

    private StudioLayout(StudioLayoutNode root)
    { Root = root; nextId = Nodes(root).Max(node => node.Id) + 1; }

    /** The built-in layout, with column widths taken from the pixel sizes given. */
    public static StudioLayout Default(float browser = 300, float inspector = 300, float changes = 190)
    {
        float left = Math.Clamp(float.IsFinite(browser) ? browser / 1540 : .195f, .1f, .3f);
        float right = Math.Clamp(float.IsFinite(inspector) ? inspector / 1540 : .195f, .1f, .3f);
        float bottom = Math.Clamp(float.IsFinite(changes) ? changes / 850 : .224f, .1f, .4f);
        return new(new()
        {
            Id = 1, Axis = StudioSplitAxis.Columns, Ratio = left,
            First = new() { Id = 2, Panel = StudioPanelKind.Browser },
            Second = new()
            {
                Id = 3, Axis = StudioSplitAxis.Columns, Ratio = 1 - right / (1 - left),
                First = new()
                {
                    Id = 4, Axis = StudioSplitAxis.Rows, Ratio = 1 - bottom,
                    First = new() { Id = 5, Panel = StudioPanelKind.Workspace },
                    Second = new() { Id = 6, Panel = StudioPanelKind.Changes }
                },
                Second = new() { Id = 7, Panel = StudioPanelKind.Inspector }
            }
        });
    }

    /** Reads a saved layout; falls back to the default when the JSON is missing or invalid. */
    public static StudioLayout Restore(JsonElement? value, float browser = 300, float inspector = 300, float changes = 190)
    {
        try
        {
            if (value is { } json && json.Deserialize<StudioLayoutNode>(Json) is { } root)
            {
                if (Valid(root, allowLegacy: true)) Migrate(root);
                if (Valid(root)) return new(root);
            }
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException) { }
        return Default(browser, inspector, changes);
    }

    /** Serializes the layout tree. */
    public JsonElement Save() => JsonSerializer.SerializeToElement(Root, Json);
    /** The node with this id, or null. */
    public StudioLayoutNode? Find(int id) => Nodes(Root).FirstOrDefault(node => node.Id == id);
    /** The leaf showing this panel kind (old graph ids map to Graph), or null. */
    public StudioLayoutNode? Find(StudioPanelKind kind) { kind = Canonical(kind); return Panels.FirstOrDefault(node => node.Panel == kind); }

    /** The panel an id opens: the old Mission Graph and Node Graph ids open the unified Graph panel. */
    public static StudioPanelKind Canonical(StudioPanelKind kind) =>
        kind is StudioPanelKind.MissionGraph or StudioPanelKind.NodeGraph ? StudioPanelKind.Graph : kind;

    /** Kinds the panel picker and Panels menu offer: every editor once, without the old graph ids. */
    public static IEnumerable<StudioPanelKind> Pickable => Enum.GetValues<StudioPanelKind>().Where(kind => Canonical(kind) == kind);

    /**
     * Saved layouts from before the unified Graph panel: the first Mission Graph or Node Graph area becomes the Graph
     * panel and a second one (both were open) becomes an empty area, so the layout stays valid and nothing is lost.
     */
    private static void Migrate(StudioLayoutNode root)
    {
        bool graph = Nodes(root).Any(node => node.IsLeaf && node.Panel == StudioPanelKind.Graph);
        foreach (var node in Nodes(root).Where(node => node.IsLeaf && Canonical(node.Panel) != node.Panel).ToArray())
        {
            node.Panel = graph ? StudioPanelKind.Empty : StudioPanelKind.Graph;
            if (node.Panel == StudioPanelKind.Empty) node.Collapsed = null;
            graph = true;
        }
    }

    /** Stateful editors move between areas; each owns one renderer and one edit context. */
    public void SetPanel(int id, StudioPanelKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        kind = Canonical(kind);
        if (Find(id) is not { IsLeaf: true } node) return;
        if (kind != StudioPanelKind.Empty && Find(kind) is { } other && other != node) other.Panel = node.Panel;
        node.Panel = kind;
    }

    /** Splits a leaf in two copies of its panel; null when it is not a leaf or the panel limit is reached. */
    public StudioLayoutNode? Split(int id, StudioSplitAxis axis)
    {
        if (!Enum.IsDefined(axis)) throw new ArgumentOutOfRangeException(nameof(axis));
        if (Find(id) is not { IsLeaf: true } node || Panels.Count >= MaximumPanels) return null;
        node.First = new() { Id = nextId++, Panel = node.Panel };
        node.Second = new() { Id = nextId++, Panel = StudioPanelKind.Empty };
        node.Panel = StudioPanelKind.Empty;
        node.Axis = axis;
        node.Ratio = .5f;
        return node.Second;
    }

    /** Closes a leaf and lets its sibling take the space; false for the root or a non-leaf. */
    public bool Close(int id)
    {
        if (Find(id) is not { IsLeaf: true } node || node == Root) return false;
        var parent = Nodes(Root).First(item => item.First == node || item.Second == node);
        var sibling = parent.First == node ? parent.Second! : parent.First!;
        if (parent == Root) Root = sibling;
        else
        {
            var grandparent = Nodes(Root).First(item => item.First == parent || item.Second == parent);
            if (grandparent.First == parent) grandparent.First = sibling;
            else grandparent.Second = sibling;
        }
        return true;
    }

    /** Returns the panel's leaf, creating it by splitting if it is not shown yet. */
    public StudioLayoutNode Show(StudioPanelKind kind)
    {
        kind = Canonical(kind);
        if (Find(kind) is { } existing) return existing;
        var target = Find(StudioPanelKind.Empty)
            ?? Split(LargestPanel(Root, 1).Node.Id, StudioSplitAxis.Columns)
            ?? Panels.First();
        SetPanel(target.Id, kind);
        return target;
    }

    private static (StudioLayoutNode Node, float Area) LargestPanel(StudioLayoutNode node, float area)
    {
        // Solforge: the AI area is never split to make room for another editor while any other area can be.
        if (node.IsLeaf) return (node, node.Panel == StudioPanelKind.AI ? 0 : area);
        var first = LargestPanel(node.First!, area * node.Ratio);
        var second = LargestPanel(node.Second!, area * (1 - node.Ratio));
        return first.Area >= second.Area ? first : second;
    }

    /** Smallest size the node's subtree needs, scaled for DPI. */
    public static Vector2 MinimumSize(StudioLayoutNode node, float scale = 1)
    {
        if (node.IsLeaf) return new(140 * scale, 110 * scale);
        var first = MinimumSize(node.First!, scale);
        var second = MinimumSize(node.Second!, scale);
        return node.Axis == StudioSplitAxis.Columns
            ? new(first.X + second.X + 5 * scale, Math.Max(first.Y, second.Y))
            : new(Math.Max(first.X, second.X), first.Y + second.Y + 5 * scale);
    }

    private static IEnumerable<StudioLayoutNode> Nodes(StudioLayoutNode root)
    {
        yield return root;
        if (root.First is { } first) foreach (var node in Nodes(first)) yield return node;
        if (root.Second is { } second) foreach (var node in Nodes(second)) yield return node;
    }

    /** @param allowLegacy Accepts the old graph ids (each once) so Migrate can fold them into Graph. */
    private static bool Valid(StudioLayoutNode root, bool allowLegacy = false)
    {
        HashSet<int> ids = [];
        HashSet<StudioPanelKind> panels = [];
        int count = 0;
        return Visit(root, 0);

        bool Visit(StudioLayoutNode node, int depth)
        {
            if (depth > MaximumPanels || node.Id is <= 0 or >= int.MaxValue - 64 || !ids.Add(node.Id)
                || !Enum.IsDefined(node.Panel) || !allowLegacy && Canonical(node.Panel) != node.Panel || !Enum.IsDefined(node.Axis) || !float.IsFinite(node.Ratio)
                || node.Ratio is <= 0 or >= 1) return false;
            if (node.IsLeaf) return ++count <= MaximumPanels
                && (node.Panel == StudioPanelKind.Empty || panels.Add(node.Panel));
            return node.First is not null && node.Second is not null && node.Panel == StudioPanelKind.Empty
                && Visit(node.First, depth + 1) && Visit(node.Second, depth + 1);
        }
    }
}
