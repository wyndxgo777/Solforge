/**
 * File: Node type model (pins, properties, categories) and the NodeLibrary registry; the node lists are in
 * NodeLibrary.Core/Material/Mission/Weapon.cs.
 */
namespace Solforge.Graphs;

/** Which graphs a node type can appear in. */
[Flags]
public enum GraphKind { None = 0, Material = 1, Weapon = 2, Mission = 4, All = Material | Weapon | Mission }

/** How a node property is edited. Tag takes a hash (0x...) or a name; Code is multi-line text. */
public enum PropertyKind { Text, Number, Integer, Bool, Choice, Color, Tag, Code }

/**
 * A pin on a node type. An input with Multi takes many links (lists such as stats or perks); every other input takes one.
 * An unlinked data input uses the node's property of the same id, else Default.
 */
public sealed record PinDef(string Id, string Name, PinType Type, bool Output, string? Default = null, bool Multi = false,
    bool Required = false);

/** A user-editable node property: id, label, kind, default, optional choices and help text. */
public sealed record PropertyDef(string Id, string Name, PropertyKind Kind, string Default = "",
    IReadOnlyList<string>? Choices = null, string Help = "");

/** A node type: its pins, properties and where it shows in the add menu. */
public sealed record NodeType(string Id, string Title, string Category, GraphKind Kinds, IReadOnlyList<PinDef> Pins,
    IReadOnlyList<PropertyDef> Properties, string Description = "", string Keywords = "", bool Hidden = false)
{
    public IEnumerable<PinDef> Inputs => Pins.Where(pin => !pin.Output);
    public IEnumerable<PinDef> Outputs => Pins.Where(pin => pin.Output);
    /** Pin of this type by direction and id, or null. */
    public PinDef? Pin(string id, bool output) => Pins.FirstOrDefault(pin => pin.Output == output && pin.Id == id);
    /** Property definition by id, or null. */
    public PropertyDef? Property(string id) => Properties.FirstOrDefault(property => property.Id == id);
    public bool IsEvent => !Inputs.Any(pin => pin.Type == PinType.Exec) && Outputs.Any(pin => pin.Type == PinType.Exec);
}

/** One add-menu row: the type and, when the menu opened from a dragged pin, the pin to link. */
public sealed record NodeSearchResult(NodeType Type, string? PinId, int Score, bool NeedsConversion);

/** The built-in node types and the context-sensitive add-menu search. */
public static partial class NodeLibrary
{
    /** All registered node types by id. */
    private static readonly Dictionary<string, NodeType> types = new(StringComparer.Ordinal);

    /** Registers the built-in node types once. */
    static NodeLibrary()
    {
        foreach (var type in CoreTypes().Concat(MaterialTypes()).Concat(ItemNodeTypes()).Concat(MissionTypes()))
            types[type.Id] = type;
    }

    public static IReadOnlyCollection<NodeType> All => types.Values;

    /** Looks up a node type by id, or null. */
    public static NodeType? Find(string id) => types.GetValueOrDefault(id);

    /** Adds or replaces a node type, such as SDK functions the editor loads at run time. */
    public static void Register(NodeType type) { lock (types) types[type.Id] = type; }

    /** Node types usable in a graph kind. */
    public static IEnumerable<NodeType> For(GraphKind kind) => types.Values.Where(type => (type.Kinds & kind) != 0);

    /**
     * The add menu: types for this graph kind matching every word of query (title, category, keywords), best first.
     * With a dragged pin, only types with a pin that can link to it are offered, and PinId names that pin.
     */
    public static IReadOnlyList<NodeSearchResult> Search(GraphKind kind, string query, PinType? dragged = null,
        bool draggedIsOutput = true, IEnumerable<NodeType>? extra = null)
    {
        string[] words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<NodeSearchResult>();
        foreach (var type in For(kind).Concat(extra ?? Enumerable.Empty<NodeType>()))
        {
            if (type.Hidden) continue;
            string title = type.Title.ToLowerInvariant();
            string haystack = $"{title} {type.Category.ToLowerInvariant()} {type.Keywords.ToLowerInvariant()} {type.Id}";
            if (!words.All(haystack.Contains)) continue;
            int score = 0;
            if (words.Length > 0)
            {
                if (title.StartsWith(words[0], StringComparison.Ordinal)) score += 40;
                else if (title.Split(' ').Any(part => part.StartsWith(words[0], StringComparison.Ordinal))) score += 25;
                else if (title.Contains(words[0], StringComparison.Ordinal)) score += 10;
            }
            string? pinId = null; bool conversion = false;
            if (dragged is { } pinType)
            {
                // A dragged output wants an input here, and the reverse.
                var candidates = type.Pins.Where(pin => pin.Output != draggedIsOutput).ToList();
                var exact = candidates.FirstOrDefault(pin => draggedIsOutput ? PinTypes.Compatible(pinType, pin.Type) && pin.Type != PinType.Any
                    : PinTypes.Compatible(pin.Type, pinType) && pin.Type != PinType.Any);
                var wild = candidates.FirstOrDefault(pin => draggedIsOutput ? PinTypes.Compatible(pinType, pin.Type) : PinTypes.Compatible(pin.Type, pinType));
                var converted = candidates.FirstOrDefault(pin => draggedIsOutput ? PinTypes.Connectable(pinType, pin.Type) : PinTypes.Connectable(pin.Type, pinType));
                var pick = exact ?? wild ?? converted;
                if (pick is null) continue;
                pinId = pick.Id;
                conversion = exact is null && wild is null;
                score += exact is not null ? (pick.Type == pinType ? 30 : 20) : wild is not null ? 10 : 0;
            }
            results.Add(new(type, pinId, score, conversion));
        }
        return results.OrderByDescending(result => result.Score).ThenBy(result => result.Type.Category, StringComparer.Ordinal)
            .ThenBy(result => result.Type.Title, StringComparer.Ordinal).ToList();
    }

    // Builders keep the tables below short.
    internal static PinDef In(string id, string name, PinType type, string? value = null, bool multi = false, bool required = false) =>
        new(id, name, type, false, value, multi, required);
    /** Builds an output pin definition. */
    internal static PinDef Out(string id, string name, PinType type) => new(id, name, type, true);
    internal static PinDef ExecIn => new("exec", "", PinType.Exec, false);
    internal static PinDef Then => new("then", "", PinType.Exec, true);
    /** Builds a property definition (choices optional). */
    internal static PropertyDef Prop(string id, string name, PropertyKind kind, string value = "", string help = "",
        params string[] choices) => new(id, name, kind, value, choices.Length == 0 ? null : choices, help);
    /** Builds a node type definition used by the NodeLibrary.* lists. */
    internal static NodeType Node(string id, string title, string category, GraphKind kinds, string description,
        PinDef[] pins, PropertyDef[]? properties = null, string keywords = "", bool hidden = false) =>
        new(id, title, category, kinds, pins, properties ?? Array.Empty<PropertyDef>(), description, keywords, hidden);
}
