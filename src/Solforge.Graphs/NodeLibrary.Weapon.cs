/**
 * File: Weapon, item and custom-perk node types; compiled by GraphCompilers.cs (WeaponGraphCompiler) and
 * PerkLowering.cs.
 */
namespace Solforge.Graphs;

/**
 * Weapon and item graph nodes. They compile to the item edit pipeline (apply_item_edit ops: set_field, add_stat,
 * remove_stat, add_perk, remove_perk) against the target item. A Custom Perk (Studio.md Phases 26-27) compiles to
 * rule descriptors, then PerkLowering turns them into a native sandbox perk program that the editor packs into a new
 * perk tag and plug item on one of the weapon's sockets.
 */
public static partial class NodeLibrary
{
    public const string WeaponOutput = "item.output";
    public const string CustomPerk = "perk.custom";

    public static readonly string[] Frames =
        ["Adaptive", "Aggressive", "Precision", "Rapid-Fire", "Lightweight", "High-Impact", "Vortex", "Wave", "Compressed Wave"];

    public static readonly string[] WeaponClasses =
    [
        "Auto Rifle", "Hand Cannon", "Pulse Rifle", "Scout Rifle", "Sidearm", "Submachine Gun", "Bow", "Shotgun", "Sniper Rifle",
        "Fusion Rifle", "Trace Rifle", "Grenade Launcher", "Rocket Launcher", "Linear Fusion Rifle", "Machine Gun", "Sword"
    ];

    public static readonly string[] Stats =
        ["Impact", "Range", "Stability", "Handling", "Reload Speed", "Rounds Per Minute", "Magazine", "Aim Assistance",
         "Zoom", "Recoil Direction", "Inventory Size", "Blast Radius", "Velocity", "Charge Time", "Draw Time", "Accuracy"];

    /** Perk events (Studio.md Phase 26), with the game moments they stand for. */
    public static readonly (string Id, string Title)[] PerkEvents =
        [("on_hit", "On Hit"), ("on_precision_hit", "On Precision Hit"), ("on_kill", "On Kill"), ("on_precision_kill", "On Precision Kill"),
         ("on_reload", "On Reload"), ("on_ability_use", "On Ability Use")];

    /** Yields the weapon/item and custom-perk node type definitions. */
    private static IEnumerable<NodeType> ItemNodeTypes()
    {
        const GraphKind kind = GraphKind.Weapon;
        yield return Node(WeaponOutput, "Weapon Output", "Output", kind,
            "The item this graph edits. Name, description, fields, stats and perks linked here compile to item edits.",
            [In("name", "Name", PinType.String), In("description", "Description", PinType.String),
             In("fields", "Fields", PinType.Field, multi: true), In("stats", "Stats", PinType.Stat, multi: true),
             In("perks", "Perks", PinType.Perk, multi: true)],
            [Prop("target", "Item", PropertyKind.Tag, "", "Item tag (0x...), hash or exact name")], "item result root final");
        yield return Node("item.stat", "Stat", "Stats", kind, "Sets an investment stat to Value (adds it when missing).",
            [In("value", "Value", PinType.Float, "50"), Out("stat", "", PinType.Stat)],
            [Prop("stat", "Stat", PropertyKind.Choice, "Impact", "Stat name or index", Stats)], "impact range stability handling reload");
        yield return Node("item.remove_stat", "Remove Stat", "Stats", kind, "Removes an investment stat.",
            [Out("stat", "", PinType.Stat)], [Prop("stat", "Stat", PropertyKind.Choice, "Impact", "Stat name or index", Stats)]);
        yield return Node("item.perk", "Perk", "Perks", kind, "Adds a perk by name or index (search_perks).",
            [Out("perk", "", PinType.Perk)], [Prop("perk", "Perk", PropertyKind.Text, "", "Perk name or index")], "trait plug socket");
        yield return Node("item.remove_perk", "Remove Perk", "Perks", kind, "Removes a perk by name or index.",
            [Out("perk", "", PinType.Perk)], [Prop("perk", "Perk", PropertyKind.Text, "", "Perk name or index")]);
        yield return Node("item.archetype", "Archetype", "Archetype", kind,
            "The weapon frame: adds the intrinsic '<Frame> Frame' perk. Weapon type is shown for reference and checked against the target.",
            [Out("perk", "Frame", PinType.Perk)],
            [Prop("type", "Weapon type", PropertyKind.Choice, "Hand Cannon", "", WeaponClasses),
             Prop("frame", "Frame", PropertyKind.Choice, "Adaptive", "", Frames)], "intrinsic frame rpm");
        yield return Node("item.field", "Set Field", "Fields", kind, "Sets an editable item field (get_item lists them).",
            [In("value", "Value", PinType.String), Out("field", "", PinType.Field)],
            [Prop("field", "Field", PropertyKind.Text, "", "Field name from get_item")], "property set");
        yield return Node("item.visuals", "Visuals", "Visuals", kind,
            "Icon: an item whose icon to share (the item workflow's Icon from item); empty leaves it alone.",
            [Out("field", "", PinType.Field)], [Prop("icon", "Icon from item", PropertyKind.Tag)], "icon image look model");
        yield return Node("item.rarity", "Rarity", "Fields", kind, "Sets the tier byte (investment +186): Common 1 to Exotic 5.",
            [Out("field", "", PinType.Field)],
            [Prop("rarity", "Rarity", PropertyKind.Choice, "Legendary", "", "None", "Common", "Uncommon", "Rare", "Legendary", "Exotic")], "tier exotic legendary");

        // Custom perk behaviour (Phases 26-27): Custom Perk -> event -> conditions -> actions; Stat nodes on Stats are the plug's stats.
        yield return Node(CustomPerk, "Custom Perk", "Perk Behavior", kind,
            "A new perk: a plug with its own sandbox perk, installed in a weapon socket. Link it to Weapon Output and to its events; "
            + "Stat nodes linked to Stats are stat bonuses while it is socketed.",
            [In("stats", "Stats", PinType.Stat, multi: true), Out("perk", "", PinType.Perk)],
            [Prop("name", "Name", PropertyKind.Text, "New Perk"), Prop("description", "Description", PropertyKind.Text),
             Prop("icon", "Icon PNG", PropertyKind.Text, "", "PNG path (absolute or project-relative); empty keeps the socket's current perk icon"),
             Prop("socket", "Socket", PropertyKind.Integer, "-1", "Weapon socket index from get_item; -1 uses the trait socket (type 92)")],
            "new trait behavior plug");
        foreach (var (id, title) in PerkEvents)
            yield return Node("perk." + id, title, "Perk Events", kind, $"Runs when the wielder triggers '{title}'.",
                [In("perk", "Perk", PinType.Perk, required: true), Then], keywords: "event trigger");
        yield return Node("perk.health_below", "Health Below", "Perk Conditions", kind, "Continues while the wielder's health fraction is below Threshold.",
            [ExecIn, In("threshold", "Threshold", PinType.Float, "0.5"), Then], keywords: "condition hp");
        yield return Node("perk.magazine_percentage", "Magazine Percentage", "Perk Conditions", kind, "Continues when the magazine is at or below Percent.",
            [ExecIn, In("percent", "Percent", PinType.Float, "50"), Then], keywords: "condition ammo");
        yield return Node("perk.has_buff", "Has Buff", "Perk Conditions", kind, "Continues while the wielder has the named buff.",
            [ExecIn, In("buff", "Buff", PinType.String), Then], keywords: "condition status");
        yield return Node("perk.target_type", "Target Type", "Perk Conditions", kind, "Continues when the target is of this type.",
            [ExecIn, Then], [Prop("target", "Target", PropertyKind.Choice, "Any", "", "Any", "Minor", "Major", "Boss", "Guardian", "Vehicle")],
            "condition enemy rank");
        yield return Node("perk.add_damage_scalar", "Add Damage Scalar", "Perk Actions", kind, "Multiplies outgoing damage for Duration seconds.",
            [ExecIn, In("scalar", "Scalar", PinType.Float, "1.2"), In("duration", "Duration", PinType.Float, "5"), In("stacks", "Max stacks", PinType.Int, "1"), Then],
            keywords: "buff damage rampage kill clip");
        yield return Node("perk.refill_magazine", "Refill Magazine", "Perk Actions", kind, "Refills Percent of the magazine from reserves.",
            [ExecIn, In("percent", "Percent", PinType.Float, "100"), Then], keywords: "ammo reload outlaw");
        yield return Node("perk.grant_super_energy", "Grant Super Energy", "Perk Actions", kind, "Grants Percent super energy.",
            [ExecIn, In("percent", "Percent", PinType.Float, "5"), Then], keywords: "super ability");
        yield return Node("perk.spawn_orb", "Spawn Orb", "Perk Actions", kind, "Spawns orbs of power at the target.",
            [ExecIn, In("count", "Count", PinType.Int, "1"), Then], keywords: "orb power");
        yield return Node("perk.explode", "Spawn Explosion", "Perk Actions", kind, "Spawns an explosion at the target (dragonfly style).",
            [ExecIn, In("damage", "Damage", PinType.Float, "100"), In("radius", "Radius", PinType.Float, "3"), Then], keywords: "dragonfly blast");
        yield return Node("perk.heal", "Heal", "Perk Actions", kind, "Restores Percent health to the wielder.",
            [ExecIn, In("percent", "Percent", PinType.Float, "10"), Then], keywords: "life steal health");
    }
}
