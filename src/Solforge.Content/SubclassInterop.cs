using System.Text;
using System.Text.Json.Nodes;

namespace Solforge.Content;

/** New subclasses start from the shipped entry layout, so the server's rules apply to them unchanged. */
public static class SubclassTemplates
{
    public static SubclassDefinition New(string id, string name, GuardianClass guardianClass, string element)
    {
        SubclassNode Node(string nodeId, string label, SubclassRole role, int? entry, string description = "") =>
            new() { Id = nodeId, Name = label, Role = role, Entry = entry, Description = description };
        var subclass = new SubclassDefinition
        {
            Id = id, Name = name, Class = guardianClass, Element = element, Source = "template:shipped-layout",
            Description = $"A {element} {guardianClass.ToString().ToLowerInvariant()} subclass.",
            Columns =
            [
                new() { Id = "super", Name = "Super", Nodes = [Node("super-lane", name + " super", SubclassRole.Lane, SubclassLayout.SuperEntry)] },
                new() { Id = "grenade", Name = "Grenade", Nodes = [.. SubclassLayout.GrenadeEntries.Select((entry, i) => Node($"grenade-{i + 1}", $"Grenade {i + 1}", SubclassRole.Grenade, entry))] },
                new() { Id = "jump", Name = "Movement", Nodes = [.. SubclassLayout.JumpEntries.Select((entry, i) => Node($"jump-{i + 1}", $"Jump {i + 1}", SubclassRole.Jump, entry))] },
                new() { Id = "class", Name = "Class ability", Nodes = [.. SubclassLayout.ClassEntries.Select((entry, i) => Node($"class-{i + 1}", $"Class ability {i + 1}", SubclassRole.Class, entry))] }
            ]
        };
        for (int t = 0; t < 3; t++)
        {
            int[] entries = SubclassLayout.TreeEntries[t];
            string prefix = $"t{t + 1}";
            subclass.Trees.Add(new()
            {
                Index = t + 1, Name = $"Path {t + 1}",
                Nodes =
                [
                    t == 2 ? Node(prefix + "-variant", "Super variant", SubclassRole.Variant, entries[0], "Changes the lane super.")
                           : Node(prefix + "-super", $"Path {t + 1} super", SubclassRole.Super, entries[0]),
                    Node(prefix + "-melee", $"Path {t + 1} melee", SubclassRole.Melee, entries[1]),
                    Node(prefix + "-perk-1", $"Path {t + 1} perk 1", SubclassRole.Perk, entries[2]),
                    Node(prefix + "-perk-2", $"Path {t + 1} perk 2", SubclassRole.Perk, entries[3])
                ]
            });
        }
        subclass.Trees[2].Nodes[3].ModifiesSuper = true;
        subclass.DefaultBuild = new()
        {
            Super = "super-lane", Grenade = "grenade-1", Melee = "t1-melee", ClassAbility = "class-1", Movement = "jump-1",
            Perks = ["t1-perk-1", "t1-perk-2"]
        };
        return subclass;
    }

    /** A D2 3.0 starter set: two aspects with fragment slots and three fragments. */
    public static void AddAbilities3(SubclassDefinition subclass)
    {
        subclass.Aspects.Add(new() { Id = "aspect-1", Name = "Aspect 1", Role = SubclassRole.Aspect, FragmentSlots = 2 });
        subclass.Aspects.Add(new() { Id = "aspect-2", Name = "Aspect 2", Role = SubclassRole.Aspect, FragmentSlots = 1 });
        for (int i = 1; i <= 3; i++) subclass.Fragments.Add(new() { Id = $"fragment-{i}", Name = $"Fragment {i}", Role = SubclassRole.Fragment });
    }
}

/** Imports from the Sol server and the editor, and the server's subclass_nodes.tsv. */
public static class SubclassInterop
{
    /**
     * From the Sol server's GET /api/subclass answer (sol_subclass_api.h): the whole answer (its
     * "subclass" member is used) or that member alone.
     */
    public static SubclassDefinition FromServerView(JsonNode view, string? id = null)
    {
        var root = view as JsonObject ?? throw new ArgumentException("The server view must be a JSON object.");
        var subclassObject = root["subclass"] as JsonObject ?? root;
        string name = (string?)subclassObject["name"] ?? "Subclass";
        var subclass = new SubclassDefinition
        {
            Id = id ?? Slug(name), Name = name, Element = ((string?)subclassObject["element"] ?? "").ToLowerInvariant() is { Length: > 0 } element ? element : "arc",
            Class = ParseClass((string?)root["class"] ?? (string?)subclassObject["class"]),
        };
        if (subclassObject["hash"] is JsonValue hashValue && hashValue.TryGetValue(out long hash)) subclass.ItemHash = ContentDocument.Hex((uint)hash);
        subclass.Source = "server:" + (subclass.ItemHash ?? name);
        if (subclassObject["limits"] is JsonObject limits)
        {
            subclass.Limits.Perks = (int?)limits["perks"] ?? subclass.Limits.Perks;
            subclass.Limits.PerTree = (int?)limits["per_tree"] ?? subclass.Limits.PerTree;
            subclass.Limits.SelectableNodes = subclass.Limits.Perks + 1;
        }
        if (subclassObject["slots"] is JsonArray slots)
            foreach (var slot in slots.OfType<JsonObject>())
            {
                string slotName = (string?)slot["slot"] ?? "slot";
                var column = new SubclassColumn { Id = slotName, Name = ColumnTitle(slotName) };
                foreach (var node in (slot["nodes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) column.Nodes.Add(Node(node));
                // Melee picks live in the paths; a melee column of path nodes would repeat their entries.
                if (column.Nodes.Count > 0 && !(slotName == "melee" && column.Nodes.All(n => n.Entry is { } e && SubclassLayout.SlotOf(e).StartsWith("tree", StringComparison.Ordinal))))
                    subclass.Columns.Add(column);
            }
        if (subclassObject["trees"] is JsonArray trees)
            foreach (var tree in trees.OfType<JsonObject>())
            {
                var path = new SubclassTree { Index = (int?)tree["index"] ?? subclass.Trees.Count + 1, Name = (string?)tree["name"] ?? "", Description = (string?)tree["description"] ?? "" };
                foreach (var node in (tree["nodes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) path.Nodes.Add(Node(node));
                subclass.Trees.Add(path);
            }
        // A node the server lists both as a slot choice and in a path stays in the path.
        var inTrees = subclass.Trees.SelectMany(tree => tree.Nodes).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var column in subclass.Columns) column.Nodes.RemoveAll(node => inTrees.Contains(node.Id));
        subclass.Columns.RemoveAll(column => column.Nodes.Count == 0);
        return subclass;

        static SubclassNode Node(JsonObject node)
        {
            int? entry = (int?)node["entry"];
            string role = ((string?)node["role"] ?? "perk").ToLowerInvariant();
            var parsed = Enum.TryParse<SubclassRole>(role, true, out var value) ? value : SubclassRole.Perk;
            return new()
            {
                Id = entry is { } e ? $"e{e}" : Slug((string?)node["name"] ?? "node"), Entry = entry, Role = parsed,
                Name = (string?)node["name"] is { Length: > 0 } label ? label : $"Entry {entry}", Description = (string?)node["description"] ?? ""
            };
        }
    }

    /**
     * From get_item's answer for a subclass item: each socket becomes a node at the entry with the
     * same index, placed by the shipped layout. Socket order is assumed to follow the entry order; check
     * the names against the in-game screen (as build-subclass-names.py warns).
     */
    public static SubclassDefinition FromItem(JsonNode item, GuardianClass guardianClass, string? id = null)
    {
        var root = item as JsonObject ?? throw new ArgumentException("The item must be a JSON object.");
        string name = (string?)root["name"] ?? "Subclass";
        string text = (name + " " + (string?)root["description"]).ToLowerInvariant();
        string element = text.Contains("solar") ? "solar" : text.Contains("void") ? "void" : "arc";
        var subclass = SubclassTemplates.New(id ?? Slug(name), name, guardianClass, element);
        subclass.Description = (string?)root["description"] ?? "";
        subclass.DefaultBuild = null;
        if (root["hash"] is JsonValue hashValue && hashValue.TryGetValue(out long hash)) subclass.ItemHash = ContentDocument.Hex((uint)hash);
        subclass.Source = "item:" + ((string?)root["tag"] ?? subclass.ItemHash ?? name);
        var byEntry = subclass.AllNodes().Where(row => row.Node.Entry is not null).ToDictionary(row => row.Node.Entry!.Value, row => row.Node);
        foreach (var socket in (root["sockets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            if ((int?)socket["index"] is { } index && byEntry.TryGetValue(index, out var node) && (string?)socket["plug"] is { Length: > 0 } plug && plug != "None")
                node.Name = plug;
        foreach (var stat in (root["stats"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            if ((string?)stat["name"] is { Length: > 0 } statName && (int?)stat["value"] is { } value && value != 0)
                subclass.Stats[statName] = value;
        return subclass;
    }

    /** Rows for Sunrise\data\subclass_nodes.tsv: subclass, entry or treeN, name, description. */
    public static string NodeNamesTsv(IEnumerable<SubclassDefinition> subclasses)
    {
        var text = new StringBuilder();
        text.Append("# Written by Solforge from content/subclass; rows win over the server's built-in names.\n");
        foreach (var subclass in subclasses)
        {
            string key = subclass.ItemHash ?? subclass.Name;
            foreach (var tree in subclass.Trees.Where(tree => tree.Name.Length > 0))
                Row(key, "tree" + tree.Index, tree.Name, tree.Description);
            foreach (var (node, _, _, _) in subclass.AllNodes().Where(row => row.Node.Entry is not null).OrderBy(row => row.Node.Entry))
                Row(key, node.Entry!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), node.Name, node.Description);
        }
        return text.ToString();

        void Row(string key, string slot, string name, string description) =>
            text.Append(Clean(key)).Append('\t').Append(slot).Append('\t').Append(Clean(name)).Append('\t').Append(Clean(description)).Append('\n');
    }

    private static string Clean(string text) => string.Join(' ', text.Replace('\t', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static GuardianClass ParseClass(string? text) => Enum.TryParse<GuardianClass>(text, true, out var value) ? value : GuardianClass.Hunter;

    private static string ColumnTitle(string slot) => slot switch
    {
        "super" => "Super", "grenade" => "Grenade", "melee" => "Melee", "class" => "Class ability", "jump" => "Movement", _ => slot
    };

    /** A valid content id from a display name. */
    public static string Slug(string name)
    {
        var text = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') text.Append(c);
            else if (text.Length > 0 && text[^1] != '-') text.Append('-');
        string slug = text.ToString().Trim('-');
        if (slug.Length > 64) slug = slug[..64].TrimEnd('-');
        return slug.Length == 0 ? "item" : slug;
    }
}
