/**
 * File: Subclass content model (nodes, trees, limits); rules are in SubclassRules.cs and conversion from the server
 * layout in SubclassInterop.cs.
 */
using System.Text.Json.Serialization;

namespace Solforge.Content;

/** The guardian class a subclass belongs to. */
public enum GuardianClass { Titan, Hunter, Warlock, Any }

/**
 * Node roles. lane, super, variant, melee, perk, grenade, jump and class are the server's roles
 * (sol_subclass_api.h NODE.role); aspect and fragment are the D2 Abilities 3.0 roles (plan.md D7),
 * and passive is a D1-grid column node that only adds stats or perks.
 */
public enum SubclassRole { Lane, Super, Variant, Melee, Perk, Grenade, Jump, Class, Aspect, Fragment, Passive }

/** One selectable node: a lane choice, a path perk, an aspect or a fragment. */
public sealed class SubclassNode
{
    /** Unique within the subclass; builds name nodes by it. */
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public SubclassRole Role { get; set; } = SubclassRole.Perk;
    /** The game's socket-entry index (0-35) for nodes that exist in the shipped list; null for new nodes. */
    public int? Entry { get; set; }
    /** Optional ability document this node grants (family "ability"). */
    public string? Ability { get; set; }
    /** Sandbox perk indices the node grants (search_perks). */
    public List<int> SandboxPerks { get; set; } = [];
    /** Stat effects by stat name, e.g. {"Recovery": 10}. */
    public Dictionary<string, int> Stats { get; set; } = [];
    /** Node ids that cannot be held together with this one. */
    public List<string> Excludes { get; set; } = [];
    /** Node ids that must be held for this one to be picked. */
    public List<string> Requires { get; set; } = [];
    /** A path perk that changes the lane super (Golden Gun, Stormtrance...); refused under a path super. */
    public bool ModifiesSuper { get; set; }
    /** Aspects only: fragment slots the aspect opens. */
    public int FragmentSlots { get; set; }
}

/** A D1-style grid column: pick at most one node (exactly one when Required). */
public sealed class SubclassColumn
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Required { get; set; } = true;
    public List<SubclassNode> Nodes { get; set; } = [];
}

/** A D2 path tree: a run of up to 4 nodes the server lets the player mix (subclass_nodes.h). */
public sealed class SubclassTree
{
    /** 1..3 as the server names them (tree1..tree3). */
    public int Index { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<SubclassNode> Nodes { get; set; } = [];
}

/**
 * Selection limits. Defaults follow plan.md D6 (six selectable nodes: the super plus five perks); the
 * server enforces ServerPerks / ServerPerTree today (abilities::nodes::kMaxPerks / kMaxPerksPerPath).
 */
public sealed class SubclassLimits
{
    public const int ServerPerks = 4, ServerPerTree = 2, ServerNodes = 5;
    public int SelectableNodes { get; set; } = 6;
    public int Perks { get; set; } = 5;
    public int PerTree { get; set; } = 2;
    public int Aspects { get; set; } = 2;
}

/** A picked build; checked with SubclassRules.CheckBuild. Ids are node ids. */
public sealed class SubclassBuild
{
    public string? Super { get; set; }
    public string? Grenade { get; set; }
    public string? Melee { get; set; }
    public string? ClassAbility { get; set; }
    public string? Movement { get; set; }
    public List<string> Perks { get; set; } = [];
    public List<string> Aspects { get; set; } = [];
    public List<string> Fragments { get; set; } = [];
}

/**
 * A unified subclass (vision.md §3, master.md 4): D1 grid columns, D2 path trees, D2 3.0 aspects and
 * fragments, and stat effects, in one document. Existing subclasses import from the Sol server's
 * /api/subclass view or from an item (get_item); new ones start from SubclassTemplates.
 */
public sealed class SubclassDefinition : ContentDocument
{
    [JsonIgnore] public override string Family => ContentFamilies.Subclass;

    public GuardianClass Class { get; set; } = GuardianClass.Hunter;
    /** Damage type id: kinetic, arc, solar, void, stasis, strand or a damage_type document id. */
    public string Element { get; set; } = "arc";
    /** The subclass item's hash when it exists in game (or was cloned with start_creation). */
    public string? ItemHash { get; set; }
    /** The item a new subclass clones (Clone subclass). */
    public string? BaseItemHash { get; set; }
    /** The game's socket-entry list for this subclass, when known (server log / api). */
    public int? SocketEntryList { get; set; }
    /** The selection limits of this subclass. */
    public SubclassLimits Limits { get; set; } = new();
    public List<SubclassColumn> Columns { get; set; } = [];
    public List<SubclassTree> Trees { get; set; } = [];
    public List<SubclassNode> Aspects { get; set; } = [];
    public List<SubclassNode> Fragments { get; set; } = [];
    /** Base stat effects of equipping the subclass. */
    public Dictionary<string, int> Stats { get; set; } = [];
    public SubclassBuild? DefaultBuild { get; set; }

    /** Every node with the path that names it, in document order. */
    public IEnumerable<(SubclassNode Node, string Path, SubclassTree? Tree, SubclassColumn? Column)> AllNodes()
    {
        for (int c = 0; c < Columns.Count; c++)
            for (int n = 0; n < Columns[c].Nodes.Count; n++)
                yield return (Columns[c].Nodes[n], $"columns[{c}].nodes[{n}]", null, Columns[c]);
        for (int t = 0; t < Trees.Count; t++)
            for (int n = 0; n < Trees[t].Nodes.Count; n++)
                yield return (Trees[t].Nodes[n], $"trees[{t}].nodes[{n}]", Trees[t], null);
        for (int a = 0; a < Aspects.Count; a++) yield return (Aspects[a], $"aspects[{a}]", null, null);
        for (int f = 0; f < Fragments.Count; f++) yield return (Fragments[f], $"fragments[{f}]", null, null);
    }

    /** Finds a node by id, or null. */
    public SubclassNode? FindNode(string? id) => id is null ? null : AllNodes().Select(row => row.Node).FirstOrDefault(node => node.Id == id);

    /** Runs the subclass rules (SubclassRules.cs). */
    protected override void ValidateFamily(ContentIssues issues) => SubclassRules.Validate(this, issues);

    /** Content this subclass points at. */
    public override IEnumerable<ContentReference> References()
    {
        if (!DamageTypeDefinition.IsBuiltIn(Element)) yield return new(ContentFamilies.DamageType, Element, "element");
        foreach (var (node, path, _, _) in AllNodes())
            if (!string.IsNullOrEmpty(node.Ability)) yield return new(ContentFamilies.Ability, node.Ability, path + ".ability");
    }
}
