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

    /** Where the custom data module lands in an export (ContentExport.CustomDataPath). */
    private const string CustomData = "server/scripts/sol_custom_data.lua";

    public static IReadOnlyList<ContentFamily> All { get; } =
    [
        new(Subclass, "Subclasses", typeof(SubclassDefinition), RuntimeSupport.Partial, "server/data/subclass_nodes.tsv + " + CustomData,
            "Node and path names and descriptions are live (the server reads subclass_nodes.tsv). Selection limits and rules mirror the " +
            "server's (6 nodes: super + 5 perks, 2 per path, for every subclass). New nodes, supers, aspects and fragments are authoring " +
            "only: 86657 talent grids come from the client's ability_buckets.bin. A new subclass item comes from Clone subclass " +
            "(start_creation key subclass)."),
        new(Ability, "Abilities", typeof(AbilityDefinition), RuntimeSupport.Partial, CustomData,
            "Live: sandbox_perks join the character's sandbox perk bank while plug_hash is selected or plugged (the server builds the " +
            "character record). Authoring only: cooldown, charges, duration, damage, radius and stats are sandbox values the client " +
            "(physics host) simulates; the server serves them on GET /api/content but no client writer applies them yet."),
        new(DamageType, "Damage types", typeof(DamageTypeDefinition), RuntimeSupport.AuthoringOnly, CustomData,
            "Authoring only: damage and shield resolution (matching_shield_multiplier, against) run on the physics host, the client; " +
            "the server only senses damage fractions and has nothing to scale. Values are served on GET /api/content; custom types " +
            "stay valid references for enemies and abilities."),
        new(LootPool, "Loot pools", typeof(LootPoolDefinition), RuntimeSupport.Live, "server/scripts/loot_pools.lua",
            "The server's loot_pools settings module (server/loot/loot_pools.h): pools layer over the built-in ones by id, and enemy_kill rules " +
            "pay drops per activity, bubble or region. Copy the file to Sunrise\\scripts beside the server."),
        new(Enemy, "Enemies", typeof(EnemyDefinition), RuntimeSupport.Partial, CustomData,
            "Live: squads bind the enemy to map squads (id or slot name, '*' prefix, optional activity); the server places squads, so " +
            "their member counts (or count_scale) apply to script placements, and each death in a bound squad rolls loot_pool at " +
            "loot_chance for everyone in the activity. Authoring only: health, shields, damage_scale, resistances and level are " +
            "simulated by the physics host (the client); served on GET /api/content. Spawning stays in mission Lua."),
        new(Activity, "Activities", typeof(ActivityDefinition), RuntimeSupport.Partial, CustomData,
            "Live: fireteam_min/max are enforced at launch into packages starting with mission_tag (or map_tag), and rewards (loot " +
            "pools, items) are paid to everyone when the program bound to mission_tag reports activity_complete. Tag hashes cannot " +
            "bind: use package names. Authoring only: the objective chain and encounters are validated data the mission script runs " +
            "(require(\"sol_custom_data\")); the server has no generic objective runner."),
        new(Artifact, "Seasonal artifact", typeof(ArtifactDefinition), RuntimeSupport.Live, CustomData,
            "The first artifact (by id) replaces the native purchase rules: only perks with a plug_hash can be bought, at most " +
            "max_unlocks, and a column opens after unlock_after earlier mods; the Sol client hides rows not on it. A perk's " +
            "sandbox_perks join the perk bank while its mod is plugged. Names and descriptions shown are the mod item's own."),
    ];

    /** Finds a family by id (case-insensitive); null when unknown. */
    public static ContentFamily? Find(string? id) => All.FirstOrDefault(family => string.Equals(family.Id, id, StringComparison.OrdinalIgnoreCase));

    /** Content the docs plan as moddable that Solforge cannot edit yet, and why. */
    public static IReadOnlyList<(string Content, string Blocker)> NotYetModdable { get; } =
    [
        ("Ability behaviour (projectiles, damage, timings)", "Lives in sandbox perk and tag data with no verified writer; abilities only reference existing sandbox perks."),
        ("Damage type visuals", "Element colours and effects are client tags; custom damage types reuse a built-in element's effects."),
        ("Enemy archetypes and AI", "Combatant tags are inspect-only (ComponentsPage damage parts); new archetypes need the combatant tag writer (Phase 37)."),
        ("Activity definitions in packages", "Activity, reward and dialog tables decode read-only (list_activities, get_activity, get_loot_table, get_dialog_table; Data explorer > Game tables) but have no writer; director tags are not decoded; activities run from mission scripts and the server's activity table."),
        ("Talent-grid nodes in packages", "86657 subclass talent grids are read by the server from ability_buckets.bin, which the client writes; Solforge does not write grids."),
        ("Vendor rotations and reward timers", "Vendor sale rows are editable (apply_vendor_edit); rotation schedules are server state with no settings module."),
        ("Quests and bounties", "Quest steps are investment items plus server pursuit state (sol_pursuits); no structured quest document yet."),
        ("Combat tuning (health, damage, elements, ability timings)", "Simulated by the physics host (the client); the server " +
            "serves the values on GET /api/content but no client writer applies them yet."),
        ("Artifact perk names and art", "The artifact screen shows each mod item's own strings and icon; new mod items need item cloning."),
    ];
}
