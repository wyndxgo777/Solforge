namespace Solforge.Content;

/**
 * The shipped subclass layout and the selection rules, mirrored from the Sol server
 * (sol_subclass_names.h, state/build_data/abilities/subclass_nodes.h) so Solforge refuses what the
 * server would refuse.
 */
public static class SubclassLayout
{
    /** Socket entries per list (socket_entry_lists::kEntryCapacity). */
    public const int EntryCount = 36;
    /** The entry nobody selects. */
    public const int SprintEntry = 1;
    public const int SuperEntry = 10;
    /** Widest run one path covers (kPathRunSize). */
    public const int PathRunSize = 4;
    public static readonly int[] ClassEntries = [2, 3];
    public static readonly int[] JumpEntries = [4, 5, 6];
    public static readonly int[] GrenadeEntries = [7, 8, 9];
    public static readonly int[][] TreeEntries = [[11, 12, 13, 14], [15, 16, 17, 18], [20, 21, 22, 23]];

    /** What the shipped layout puts at an entry: "class", "jump", "grenade", "super", "sprint", "tree1".."tree3" or "". */
    public static string SlotOf(int entry)
    {
        if (entry == SprintEntry) return "sprint";
        if (entry == SuperEntry) return "super";
        if (ClassEntries.Contains(entry)) return "class";
        if (JumpEntries.Contains(entry)) return "jump";
        if (GrenadeEntries.Contains(entry)) return "grenade";
        for (int t = 0; t < TreeEntries.Length; t++) if (TreeEntries[t].Contains(entry)) return "tree" + (t + 1);
        return "";
    }
}

public static class SubclassRules
{
    private static readonly SubclassRole[] ColumnRoles =
        [SubclassRole.Lane, SubclassRole.Super, SubclassRole.Grenade, SubclassRole.Jump, SubclassRole.Class, SubclassRole.Melee, SubclassRole.Passive, SubclassRole.Perk];
    private static readonly SubclassRole[] TreeRoles = [SubclassRole.Super, SubclassRole.Variant, SubclassRole.Melee, SubclassRole.Perk];

    public static void Validate(SubclassDefinition subclass, ContentIssues issues)
    {
        if (subclass.Class == GuardianClass.Any) issues.Error("class", "A subclass belongs to titan, hunter or warlock.");
        if (string.IsNullOrWhiteSpace(subclass.Element)) issues.Error("element", "Give a damage type (arc, solar, void or a damage_type id).");
        else if (subclass.Element is "stasis" or "strand")
            issues.Warn("element", $"Build 86657 ships no {subclass.Element} assets; the subclass needs custom effects to look right in game.");
        if (subclass.ItemHash is { } item && ContentDocument.ParseHash(item) is null) issues.Error("item_hash", "Not a hash (0x... or decimal).");
        if (subclass.BaseItemHash is { } baseItem && ContentDocument.ParseHash(baseItem) is null) issues.Error("base_item_hash", "Not a hash (0x... or decimal).");
        if (subclass.ItemHash is null && subclass.BaseItemHash is null)
            issues.Warn("item_hash", "No item yet: clone a subclass item (start_creation key subclass) and set item_hash, or set base_item_hash.");

        var limits = subclass.Limits;
        if (limits.SelectableNodes < 1 || limits.SelectableNodes > 12) issues.Error("limits.selectable_nodes", "Must be 1..12.");
        if (limits.Perks < 0 || limits.Perks >= limits.SelectableNodes) issues.Error("limits.perks", "Perks must leave room for the super (perks < selectable_nodes).");
        if (limits.PerTree < 1 || (limits.Perks > 0 && limits.PerTree > limits.Perks)) issues.Error("limits.per_tree", "Must be 1..perks.");
        if (limits.Aspects < 0 || limits.Aspects > 4) issues.Error("limits.aspects", "Must be 0..4.");
        if (limits.Perks > SubclassLimits.ServerPerks || limits.PerTree > SubclassLimits.ServerPerTree)
            issues.Warn("limits", $"The server allows {SubclassLimits.ServerPerks} perks ({SubclassLimits.ServerPerTree} per path) today; " +
                "builds above that are refused in game until the six-node change (plan.md D6) lands server side.");

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = new Dictionary<int, string>();
        foreach (var (node, path, tree, column) in subclass.AllNodes())
        {
            if (!ContentDocument.IsValidId(node.Id)) issues.Error(path + ".id", "Node id must be a-z, 0-9, '_', '-' or '.'.");
            else if (!ids.TryAdd(node.Id, path)) issues.Error(path + ".id", $"Id '{node.Id}' is also used at {ids[node.Id]}.");
            if (string.IsNullOrWhiteSpace(node.Name)) issues.Error(path + ".name", "Name cannot be empty.");
            if (node.Entry is { } entry)
            {
                if (entry < 0 || entry >= SubclassLayout.EntryCount) issues.Error(path + ".entry", $"Entry must be 0..{SubclassLayout.EntryCount - 1}.");
                else if (!entries.TryAdd(entry, path)) issues.Error(path + ".entry", $"Entry {entry} is also used at {entries[entry]}.");
                else if (entry == SubclassLayout.SprintEntry) issues.Error(path + ".entry", "Entry 1 is sprint, which nobody selects.");
                if (tree is not null && SubclassLayout.SlotOf(entry) is var slot && slot.StartsWith("tree", StringComparison.Ordinal)
                    && slot != "tree" + tree.Index)
                    issues.Warn(path + ".entry", $"Entry {entry} is {slot} in the shipped layout, not tree{tree.Index}.");
            }
            if (tree is not null && !TreeRoles.Contains(node.Role)) issues.Error(path + ".role", $"A path node is super, variant, melee or perk, not {node.Role}.");
            if (column is not null && !ColumnRoles.Contains(node.Role)) issues.Error(path + ".role", $"A column node cannot be {node.Role}.");
            if (path.StartsWith("aspects", StringComparison.Ordinal) && node.Role != SubclassRole.Aspect) issues.Error(path + ".role", "Aspects have role aspect.");
            if (path.StartsWith("fragments", StringComparison.Ordinal) && node.Role != SubclassRole.Fragment) issues.Error(path + ".role", "Fragments have role fragment.");
            if (node.Role == SubclassRole.Aspect && (node.FragmentSlots < 0 || node.FragmentSlots > 5)) issues.Error(path + ".fragment_slots", "Must be 0..5.");
            if (node.Role != SubclassRole.Aspect && node.FragmentSlots != 0) issues.Warn(path + ".fragment_slots", "Only aspects open fragment slots.");
            if (node.ModifiesSuper && tree is null) issues.Warn(path + ".modifies_super", "Only path perks modify the lane super.");
            foreach (int perk in node.SandboxPerks) if (perk < 0 || perk > ushort.MaxValue) issues.Error(path + ".sandbox_perks", $"Perk index {perk} is out of range.");
            foreach (var (stat, value) in node.Stats) if (value is < -100 or > 100) issues.Warn(path + ".stats." + stat, "Stat effects beyond ±100 exceed every retail cap.");
        }
        // Links are checked once every id is known.
        foreach (var (node, path, _, _) in subclass.AllNodes())
        {
            foreach (string other in node.Excludes)
                if (other == node.Id) issues.Error(path + ".excludes", "A node cannot exclude itself.");
                else if (!ids.ContainsKey(other)) issues.Error(path + ".excludes", $"No node '{other}'.");
            foreach (string other in node.Requires)
                if (other == node.Id) issues.Error(path + ".requires", "A node cannot require itself.");
                else if (!ids.ContainsKey(other)) issues.Error(path + ".requires", $"No node '{other}'.");
                else if (node.Excludes.Contains(other)) issues.Error(path + ".requires", $"'{other}' is both required and excluded.");
        }

        var trees = new HashSet<int>();
        for (int t = 0; t < subclass.Trees.Count; t++)
        {
            var tree = subclass.Trees[t];
            if (tree.Index is < 1 or > 3) issues.Error($"trees[{t}].index", "Path index must be 1..3 (tree1..tree3 on the server).");
            else if (!trees.Add(tree.Index)) issues.Error($"trees[{t}].index", $"Path {tree.Index} appears twice.");
            if (tree.Nodes.Count > SubclassLayout.PathRunSize && tree.Nodes.Any(node => node.Entry is not null))
                issues.Error($"trees[{t}].nodes", $"A shipped path holds at most {SubclassLayout.PathRunSize} nodes.");
            if (tree.Nodes.Count(node => node.Role == SubclassRole.Super) > 1) issues.Error($"trees[{t}].nodes", "A path has at most one super.");
        }
        if (subclass.Trees.Count > 3) issues.Error("trees", "At most 3 paths.");
        for (int c = 0; c < subclass.Columns.Count; c++)
        {
            var column = subclass.Columns[c];
            if (!ContentDocument.IsValidId(column.Id)) issues.Error($"columns[{c}].id", "Column id must be a-z, 0-9, '_', '-' or '.'.");
            if (column.Required && column.Nodes.Count == 0) issues.Error($"columns[{c}].nodes", "A required column needs at least one node.");
        }
        foreach (var (role, label) in new[] { (SubclassRole.Super, "super"), (SubclassRole.Grenade, "grenade"), (SubclassRole.Melee, "melee"),
            (SubclassRole.Class, "class ability"), (SubclassRole.Jump, "jump") })
            if (!subclass.AllNodes().Any(row => row.Node.Role == role || (role == SubclassRole.Super && row.Node.Role == SubclassRole.Lane)))
                issues.Error("columns", $"The subclass has no {label} node.");

        if (subclass.DefaultBuild is { } build)
            foreach (var issue in CheckBuild(subclass, build)) issues.Add(issue with { Path = "default_build" + (issue.Path.Length > 0 ? "." + issue.Path : "") });
    }

    /** The server's selection rules (subclass_nodes.h) plus the D2 3.0 aspect/fragment rules. */
    public static ContentIssues CheckBuild(SubclassDefinition subclass, SubclassBuild build)
    {
        var issues = new ContentIssues();
        var picked = new List<SubclassNode>();
        SubclassNode? One(string? id, string path, params SubclassRole[] roles)
        {
            if (string.IsNullOrEmpty(id)) { issues.Error(path, "Pick one."); return null; }
            if (subclass.FindNode(id) is not { } node) { issues.Error(path, $"No node '{id}'."); return null; }
            if (!roles.Contains(node.Role)) { issues.Error(path, $"'{id}' is a {node.Role} node, not {string.Join(" or ", roles)}."); return null; }
            picked.Add(node);
            return node;
        }
        var super = One(build.Super, "super", SubclassRole.Super, SubclassRole.Lane);
        One(build.Grenade, "grenade", SubclassRole.Grenade);
        One(build.Melee, "melee", SubclassRole.Melee);
        One(build.ClassAbility, "class_ability", SubclassRole.Class);
        One(build.Movement, "movement", SubclassRole.Jump);

        var treeOf = new Dictionary<string, SubclassTree>(StringComparer.Ordinal);
        foreach (var tree in subclass.Trees) foreach (var node in tree.Nodes) treeOf[node.Id] = tree;
        bool pathSuper = super is not null && treeOf.ContainsKey(super.Id) && super.Role == SubclassRole.Super;
        var perTree = new Dictionary<int, int>();
        var modifierTrees = new HashSet<int>();
        foreach (string id in build.Perks.Distinct())
        {
            if (subclass.FindNode(id) is not { } node) { issues.Error("perks", $"No node '{id}'."); continue; }
            if (node.Role is not (SubclassRole.Perk or SubclassRole.Variant or SubclassRole.Passive))
            { issues.Error("perks", $"'{id}' is a {node.Role} node, not a perk."); continue; }
            picked.Add(node);
            if (treeOf.TryGetValue(id, out var tree))
            {
                perTree[tree.Index] = perTree.GetValueOrDefault(tree.Index) + 1;
                if (node.ModifiesSuper || node.Role == SubclassRole.Variant)
                {
                    if (pathSuper) issues.Error("perks", $"'{id}' modifies the lane super, which a path super replaces.");
                    modifierTrees.Add(tree.Index);
                }
            }
        }
        if (build.Perks.Count != build.Perks.Distinct().Count()) issues.Error("perks", "A perk is picked twice.");
        int perks = build.Perks.Distinct().Count();
        if (perks > subclass.Limits.Perks) issues.Error("perks", $"{perks} perks; the subclass allows {subclass.Limits.Perks}.");
        if (perks + 1 > subclass.Limits.SelectableNodes) issues.Error("perks", $"Super plus {perks} perks exceeds {subclass.Limits.SelectableNodes} nodes.");
        foreach (var (tree, count) in perTree)
            if (count > subclass.Limits.PerTree) issues.Error("perks", $"Path {tree} has {count} perks; at most {subclass.Limits.PerTree}.");
        if (modifierTrees.Count > 1) issues.Error("perks", "Super modifiers from different paths exclude each other.");
        if (perks > SubclassLimits.ServerPerks) issues.Warn("perks", $"The server holds {SubclassLimits.ServerPerks} perks today.");

        foreach (var column in subclass.Columns)
        {
            int count = column.Nodes.Count(node => picked.Contains(node));
            if (count > 1) issues.Error("columns." + column.Id, $"Column '{column.Name}' allows one pick; {count} are picked.");
        }

        int slots = 0;
        foreach (string id in build.Aspects.Distinct())
        {
            if (subclass.Aspects.FirstOrDefault(node => node.Id == id) is not { } aspect) { issues.Error("aspects", $"No aspect '{id}'."); continue; }
            picked.Add(aspect); slots += aspect.FragmentSlots;
        }
        if (build.Aspects.Distinct().Count() > subclass.Limits.Aspects) issues.Error("aspects", $"At most {subclass.Limits.Aspects} aspects.");
        foreach (string id in build.Fragments.Distinct())
        {
            if (subclass.Fragments.FirstOrDefault(node => node.Id == id) is not { } fragment) { issues.Error("fragments", $"No fragment '{id}'."); continue; }
            picked.Add(fragment);
        }
        if (build.Fragments.Distinct().Count() > slots) issues.Error("fragments", $"{build.Fragments.Distinct().Count()} fragments; the picked aspects open {slots} slots.");

        var pickedIds = picked.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var node in picked)
        {
            foreach (string other in node.Excludes) if (pickedIds.Contains(other)) issues.Error("", $"'{node.Id}' excludes '{other}'.");
            foreach (string other in node.Requires) if (!pickedIds.Contains(other)) issues.Error("", $"'{node.Id}' requires '{other}'.");
        }
        return issues;
    }
}
