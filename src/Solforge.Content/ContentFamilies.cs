/**
 * File: Registry of the content families (id, title, document type, runtime support). The MCP content tools
 * (Solforge.Mcp/SolforgeTools.Content.cs) list and look up families here.
 */
namespace Solforge.Content;

/**
 * How far the game honours a family today, shown next to every editor so nothing unimplemented looks
 * functional (SOLFORGE-AI-IMPLEMENTATION-TODO P20 acceptance).
 */
public enum RuntimeSupport
{
    /** The Sol server reads the exported file and changes play. */
    Live,
    /** Part of the document is live (named in Notes); the rest is authoring only. */
    Partial,
    /** Validated and exported as data; no runtime reader yet. */
    AuthoringOnly
}

/** One content family: id, title, its document class, how far the game honours it and its output. */
public sealed record ContentFamily(string Id, string Title, Type DocumentType, RuntimeSupport Support, string Output, string Notes);

/** Every content family Solforge edits, and the content that should be moddable but is not yet. */
public static class ContentFamilies
{
    public const string Subclass = "subclass", Ability = "ability", DamageType = "damage_type", LootPool = "loot_pool",
        Enemy = "enemy", Activity = "activity", Artifact = "artifact";

    public static IReadOnlyList<ContentFamily> All { get; } =
    [
        new(Subclass, "Subclasses", typeof(SubclassDefinition), RuntimeSupport.Partial, "server/data/subclass_nodes.tsv + server/scripts/sol_content.lua",
            "Node and path names and descriptions are live (the server reads subclass_nodes.tsv). Selection limits and rules mirror the " +
            "server's. New nodes, supers, aspects and fragments are authoring only until the server reads sol_content.lua; a new subclass item " +
            "comes from Clone subclass (start_creation key subclass)."),
        new(Ability, "Abilities", typeof(AbilityDefinition), RuntimeSupport.AuthoringOnly, "server/scripts/sol_content.lua",
            "Supers, grenades, melees, class abilities, movement, aspects and fragments as data; sandbox perk indices reference real perks."),
        new(DamageType, "Damage types", typeof(DamageTypeDefinition), RuntimeSupport.AuthoringOnly, "server/scripts/sol_content.lua",
            "Kinetic, arc, solar and void are built in; custom types are data for enemies and abilities."),
        new(LootPool, "Loot pools", typeof(LootPoolDefinition), RuntimeSupport.Live, "server/scripts/loot_pools.lua",
            "The server's loot_pools settings module (server/loot/loot_pools.h): pools layer over the built-in ones by id, and enemy_kill rules " +
            "pay drops per activity, bubble or region. Copy the file to Sunrise\\scripts beside the server."),
        new(Enemy, "Enemies", typeof(EnemyDefinition), RuntimeSupport.AuthoringOnly, "server/scripts/sol_content.lua",
            "Combatant tuning (health, shields, resistances, loot) as data for mission scripts; spawning stays in mission Lua."),
        new(Activity, "Activities", typeof(ActivityDefinition), RuntimeSupport.AuthoringOnly, "server/scripts/sol_content.lua",
            "Objective chains, fireteam limits, checkpoints and rewards, validated for unreachable steps; the mission script runs them."),
        new(Artifact, "Seasonal artifact", typeof(ArtifactDefinition), RuntimeSupport.AuthoringOnly, "server/scripts/sol_content.lua",
            "Columns of mods with unlock thresholds (Studio.md Phase 38)."),
    ];

    /** Finds a family by id (case-insensitive); null when unknown. */
    public static ContentFamily? Find(string? id) => All.FirstOrDefault(family => string.Equals(family.Id, id, StringComparison.OrdinalIgnoreCase));

    /** Content the docs plan as moddable that Solforge cannot edit yet, and why. */
    public static IReadOnlyList<(string Content, string Blocker)> NotYetModdable { get; } =
    [
        ("Ability behaviour (projectiles, damage, timings)", "Lives in sandbox perk and tag data with no verified writer; abilities only reference existing sandbox perks."),
        ("Damage type visuals", "Element colours and effects are client tags; custom damage types reuse a built-in element's effects."),
        ("Enemy archetypes and AI", "Combatant tags are inspect-only (ComponentsPage damage parts); new archetypes need the combatant tag writer (Phase 37)."),
        ("Activity definitions in packages", "Activity and director tags are not decoded; activities run from mission scripts and the server's activity table."),
        ("Talent-grid nodes in packages", "86657 subclass talent grids are read by the server from ability_buckets.bin, which the client writes; Solforge does not write grids."),
        ("Vendor rotations and reward timers", "Vendor sale rows are editable (apply_vendor_edit); rotation schedules are server state with no settings module."),
        ("Quests and bounties", "Quest steps are investment items plus server pursuit state (sol_pursuits); no structured quest document yet."),
        ("Seasonal artifact in game", "No artifact UI or progression reader in 86657 or the server yet; the document is authoring only."),
    ];
}
