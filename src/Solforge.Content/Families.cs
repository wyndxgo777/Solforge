/**
 * File: The content families as data classes with validation: abilities, and the other families listed in
 * ContentFamilies.cs (enemies, activities, artifacts and more). Subclasses are in Subclass.cs.
 */
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Solforge.Content;

/** The ability categories. */
public enum AbilityKind { Super, Grenade, Melee, ClassAbility, Movement, Aspect, Fragment, Passive }

/** A super, grenade, melee, class ability, movement mode, aspect, fragment or passive as data. */
public sealed class AbilityDefinition : ContentDocument
{
    [JsonIgnore] public override string Family => ContentFamilies.Ability;
    public AbilityKind Kind { get; set; } = AbilityKind.Grenade;
    public GuardianClass Class { get; set; } = GuardianClass.Any;
    /** Damage type id (built in or a damage_type document). */
    public string Element { get; set; } = "kinetic";
    public double CooldownSeconds { get; set; }
    public int Charges { get; set; } = 1;
    public double DurationSeconds { get; set; }
    public double Damage { get; set; }
    public double Radius { get; set; }
    /** Existing sandbox perk indices this ability grants (search_perks). */
    public List<int> SandboxPerks { get; set; } = [];
    /** The plug item hash that represents it in game, when it exists. */
    public string? PlugHash { get; set; }
    public Dictionary<string, int> Stats { get; set; } = [];

    /** Checks this ability for problems the server would refuse. */
    protected override void ValidateFamily(ContentIssues issues)
    {
        if (CooldownSeconds is < 0 or > 1800) issues.Error("cooldown_seconds", "Must be 0..1800.");
        if (Charges is < 0 or > 10) issues.Error("charges", "Must be 0..10.");
        if (DurationSeconds is < 0 or > 600) issues.Error("duration_seconds", "Must be 0..600.");
        if (Damage < 0) issues.Error("damage", "Cannot be negative.");
        if (Radius is < 0 or > 100) issues.Error("radius", "Must be 0..100 metres.");
        if (Kind == AbilityKind.Super && CooldownSeconds is > 0 and < 60) issues.Warn("cooldown_seconds", "Retail supers recharge in 5-10 minutes.");
        if (PlugHash is not null && ParseHash(PlugHash) is null) issues.Error("plug_hash", "Not a hash.");
        foreach (int perk in SandboxPerks) if (perk is < 0 or > ushort.MaxValue) issues.Error("sandbox_perks", $"Perk index {perk} is out of range.");
        if (SandboxPerks.Count == 0 && PlugHash is null)
            issues.Warn("sandbox_perks", "No sandbox perk or plug: the ability is a description only until one is referenced.");
    }

    /** Content this ability points at. */
    public override IEnumerable<ContentReference> References()
    {
        if (!DamageTypeDefinition.IsBuiltIn(Element)) yield return new(ContentFamilies.DamageType, Element, "element");
    }
}

/** A damage type (element). The four 86657 elements are built in; stasis and strand are known but have no assets. */
public sealed partial class DamageTypeDefinition : ContentDocument
{
    public static readonly string[] BuiltIn = ["kinetic", "arc", "solar", "void", "stasis", "strand"];
    /** Whether an id names a built-in rather than user content. */
    public static bool IsBuiltIn(string? id) => id is not null && BuiltIn.Contains(id);

    [JsonIgnore] public override string Family => ContentFamilies.DamageType;
    /** The built-in element whose effects and shields it borrows. */
    public string BaseElement { get; set; } = "kinetic";
    public string Color { get; set; } = "#FFFFFF";
    /** Damage multiplier against shields of this type (1 = neutral; matching shields take 1.5-3 in retail). */
    public double MatchingShieldMultiplier { get; set; } = 2;
    /** Damage multiplier against other damage types' shields or resistances, by type id. */
    public Dictionary<string, double> Against { get; set; } = [];

    /** Checks this family for problems the server would refuse. */
    protected override void ValidateFamily(ContentIssues issues)
    {
        if (IsBuiltIn(Id)) issues.Error("id", $"'{Id}' is built in; give a custom type its own id.");
        if (!BuiltIn.Take(4).Contains(BaseElement)) issues.Error("base_element", "Borrow kinetic, arc, solar or void (the elements 86657 ships).");
        if (!ColorPattern().IsMatch(Color)) issues.Error("color", "Use #RRGGBB.");
        if (MatchingShieldMultiplier is < 0 or > 10) issues.Error("matching_shield_multiplier", "Must be 0..10.");
        foreach (var (other, value) in Against) if (value is < 0 or > 10) issues.Error("against." + other, "Must be 0..10.");
    }

    /** Content this document points at. */
    public override IEnumerable<ContentReference> References() =>
        Against.Keys.Where(key => !IsBuiltIn(key)).Select(key => new ContentReference(ContentFamilies.DamageType, key, "against." + key));

    /** Regex of valid colours (generated at compile time). */
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();
}

/** One enemy_kill rule of the server's loot_pools module: drops from this pool where enemies die. */
public sealed class LootKillRule
{
    public string Id { get; set; } = "";
    /** Bound activity id substring (e.g. "patrol"); empty matches any. */
    public string Activity { get; set; } = "";
    /** Bubble name substring; empty matches any. */
    public string Bubble { get; set; } = "";
    public string? BubbleHash { get; set; }
    public List<int> Region { get; set; } = [];
    /** Chance per kill, 0..1. */
    public double Chance { get; set; } = 0.05;
}

/**
 * A pool of the server's loot_pools settings module (Sunrise\scripts\loot_pools.lua). Weights key by
 * item hash; weight 0 removes an item from a built-in pool of the same id.
 */
public sealed class LootPoolDefinition : ContentDocument
{
    public static readonly string[] BuiltInPools = ["dreaming_city", "moon", "lost_ghost"];
    public static readonly string[] Catalogs = ["exotic_weapons"];

    [JsonIgnore] public override string Family => ContentFamilies.LootPool;
    public Dictionary<string, double> Any { get; set; } = [];
    public Dictionary<string, double> Titan { get; set; } = [];
    public Dictionary<string, double> Hunter { get; set; } = [];
    public Dictionary<string, double> Warlock { get; set; } = [];
    /** Items always paid in this order instead of a weighted roll. */
    public List<string> Only { get; set; } = [];
    /** A server catalog to roll from instead of the lists (exotic_weapons). */
    public string? Catalog { get; set; }
    public int Quantity { get; set; } = 1;
    /** Profile-wide item (collectible) rather than per character. */
    public bool? Profile { get; set; }
    public List<LootKillRule> EnemyKill { get; set; } = [];

    /** Checks this family for problems the server would refuse. */
    protected override void ValidateFamily(ContentIssues issues)
    {
        if (Id.Contains('.')) issues.Error("id", "Pool ids are Lua keys: use a-z, 0-9, '_' or '-'.");
        foreach (var (name, weights) in new[] { ("any", Any), ("titan", Titan), ("hunter", Hunter), ("warlock", Warlock) })
            foreach (var (hash, weight) in weights)
            {
                if (ParseHash(hash) is not { } value || value == 0) issues.Error($"{name}.{hash}", "Not an item hash.");
                if (weight < 0) issues.Error($"{name}.{hash}", "Weight cannot be negative (0 removes the item).");
            }
        for (int i = 0; i < Only.Count; i++) if (ParseHash(Only[i]) is not { } value || value == 0) issues.Error($"only[{i}]", "Not an item hash.");
        if (Catalog is not null && !Catalogs.Contains(Catalog)) issues.Warn("catalog", $"The server knows catalogs: {string.Join(", ", Catalogs)}.");
        if (Quantity is < 1 or > 9999) issues.Error("quantity", "Must be 1..9999 (the server clamps).");
        bool empty = Any.Count + Titan.Count + Hunter.Count + Warlock.Count + Only.Count == 0 && Catalog is null;
        if (empty && !BuiltInPools.Contains(Id)) issues.Error("", "The pool pays nothing: add items, 'only' or a catalog.");
        var rules = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < EnemyKill.Count; i++)
        {
            var rule = EnemyKill[i];
            if (!IsValidId(rule.Id) || rule.Id.Contains('.')) issues.Error($"enemy_kill[{i}].id", "Rule ids are Lua keys (a-z, 0-9, '_' or '-').");
            else if (!rules.Add(rule.Id)) issues.Error($"enemy_kill[{i}].id", "Repeated rule id.");
            if (rule.Chance is < 0 or > 1) issues.Error($"enemy_kill[{i}].chance", "Chance is 0..1 per kill.");
            if (rule.BubbleHash is not null && ParseHash(rule.BubbleHash) is null) issues.Error($"enemy_kill[{i}].bubble_hash", "Not a hash.");
            if (rule.Activity.Length == 0 && rule.Bubble.Length == 0 && rule.BubbleHash is null && rule.Region.Count == 0)
                issues.Warn($"enemy_kill[{i}]", "No scope: every enemy kill everywhere rolls this pool.");
        }
    }
}

/**
 * One map squad an enemy document stands for. The Sol server places squads, so it applies the member
 * counts to script placements and rolls the enemy's loot_pool on each death in the squad.
 */
public sealed class EnemySquadBinding
{
    /** The squad's id or slot name in the mission (list_mission_objects); a trailing '*' matches a prefix. */
    public string Squad { get; set; } = "";
    /** Substring of the activity id the binding is limited to; empty binds it everywhere. */
    public string Activity { get; set; } = "";
    /** Member counts by lane (0..64 each, at most 15); empty keeps the package counts times CountScale. */
    public List<int> Counts { get; set; } = [];
    /** Multiplies the package's default member counts when Counts is empty (0..10). */
    public double CountScale { get; set; } = 1;
}

/** Enemy ranks, from minor to champions. */
public enum EnemyRank { Minor, Elite, Major, Ultra, Boss, ChampionBarrier, ChampionOverload, ChampionUnstoppable }

/** Combatant tuning as data: health, shield, resistances, abilities and loot. */
public sealed class EnemyDefinition : ContentDocument
{
    public static readonly string[] Races = ["fallen", "hive", "vex", "cabal", "taken", "scorn", "guardian", "other"];

    [JsonIgnore] public override string Family => ContentFamilies.Enemy;
    public string Race { get; set; } = "fallen";
    public EnemyRank Rank { get; set; } = EnemyRank.Minor;
    /** The game's combatant or entity tag this enemy is based on (search_assets), when known. */
    public string? BaseTag { get; set; }
    public double Health { get; set; } = 100;
    /** Damage type of the shield, or null for none. */
    public string? ShieldElement { get; set; }
    public double ShieldHealth { get; set; }
    public double DamageScale { get; set; } = 1;
    /** Damage taken multipliers by damage type id (1 = neutral). */
    public Dictionary<string, double> Resistances { get; set; } = [];
    public List<string> Abilities { get; set; } = [];
    public string? LootPool { get; set; }
    /** Chance per kill (0..1) that a death in a bound squad pays from LootPool. */
    public double LootChance { get; set; } = 0.05;
    /** Map squads this enemy stands for; the server applies counts and drops to them. */
    public List<EnemySquadBinding> Squads { get; set; } = [];
    public int Level { get; set; }

    /** Checks this enemy family for problems the server would refuse. */
    protected override void ValidateFamily(ContentIssues issues)
    {
        if (!Races.Contains(Race)) issues.Warn("race", $"Known races: {string.Join(", ", Races)}.");
        if (Health is <= 0 or > 1e9) issues.Error("health", "Must be above 0.");
        if (ShieldHealth < 0) issues.Error("shield_health", "Cannot be negative.");
        if (ShieldHealth > 0 && ShieldElement is null) issues.Error("shield_element", "A shield needs a damage type.");
        if (DamageScale is <= 0 or > 100) issues.Error("damage_scale", "Must be above 0 and at most 100.");
        foreach (var (type, value) in Resistances) if (value is < 0 or > 10) issues.Error("resistances." + type, "Must be 0..10.");
        if (BaseTag is not null && ParseHash(BaseTag) is null) issues.Error("base_tag", "Not a tag hash.");
        if (Rank is EnemyRank.Boss or EnemyRank.Ultra && Health < 1000) issues.Warn("health", "Bosses usually have far more health.");
        if (LootChance is < 0 or > 1) issues.Error("loot_chance", "Chance is 0..1 per kill.");
        if (LootPool is not null && Squads.Count == 0)
            issues.Warn("loot_pool", "The server pays enemy drops per squad: bind squads, or use a loot_pools enemy_kill rule.");
        for (int i = 0; i < Squads.Count; i++)
        {
            var squad = Squads[i];
            if (string.IsNullOrWhiteSpace(squad.Squad) || squad.Squad.Trim() == "*") issues.Error($"squads[{i}].squad", "Name the squad (id or slot name).");
            if (squad.Counts.Count > 15) issues.Error($"squads[{i}].counts", "A squad has at most 15 member lanes.");
            if (squad.Counts.Any(count => count is < 0 or > 64)) issues.Error($"squads[{i}].counts", "Each count is 0..64.");
            if (squad.CountScale is < 0 or > 10) issues.Error($"squads[{i}].count_scale", "Must be 0..10.");
            if (squad.Counts.Count > 0 && squad.CountScale != 1) issues.Warn($"squads[{i}].count_scale", "Ignored: counts are set.");
        }
    }

    /** Content this enemy points at. */
    public override IEnumerable<ContentReference> References()
    {
        if (ShieldElement is not null && !DamageTypeDefinition.IsBuiltIn(ShieldElement)) yield return new(ContentFamilies.DamageType, ShieldElement, "shield_element");
        foreach (string type in Resistances.Keys) if (!DamageTypeDefinition.IsBuiltIn(type)) yield return new(ContentFamilies.DamageType, type, "resistances." + type);
        for (int i = 0; i < Abilities.Count; i++) yield return new(ContentFamilies.Ability, Abilities[i], $"abilities[{i}]");
        if (LootPool is not null && !LootPoolDefinition.BuiltInPools.Contains(LootPool)) yield return new(ContentFamilies.LootPool, LootPool, "loot_pool");
    }
}

/** The activity types. */
public enum ActivityType { Patrol, Story, Strike, Nightfall, Raid, Dungeon, LostSector, PublicEvent, Crucible, Gambit, Custom }

/** One encounter of an activity. */
public sealed class ActivityEncounter
{
    public string Enemy { get; set; } = "";
    public int Count { get; set; } = 1;
}

/** One objective of an activity. */
public sealed class ActivityObjective
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    /** kill, interact, defend, boss, travel, timer, custom. */
    public string Kind { get; set; } = "kill";
    public int Count { get; set; } = 1;
    public double TimerSeconds { get; set; }
    public bool Checkpoint { get; set; }
    public List<ActivityEncounter> Encounters { get; set; } = [];
    /** Objectives that follow this one; none means it completes the activity. */
    public List<string> Next { get; set; } = [];
}

/** Rewards of an activity. */
public sealed class ActivityReward
{
    public List<string> LootPools { get; set; } = [];
    /** Item hash to quantity. */
    public Dictionary<string, int> Items { get; set; } = [];
}

/** An activity as data: objective chain, fireteam limits, checkpoints and rewards. */
public sealed class ActivityDefinition : ContentDocument
{
    [JsonIgnore] public override string Family => ContentFamilies.Activity;
    public ActivityType Type { get; set; } = ActivityType.Strike;
    public string Destination { get; set; } = "";
    /** Map and mission tags (list_maps / list_missions). */
    public string? MapTag { get; set; }
    public string? MissionTag { get; set; }
    public int FireteamMin { get; set; } = 1;
    public int FireteamMax { get; set; } = 3;
    public int RecommendedPower { get; set; }
    public string Start { get; set; } = "";
    public List<ActivityObjective> Objectives { get; set; } = [];
    /** The activity rewards. */
    public ActivityReward Rewards { get; set; } = new();

    /** Checks this activity for problems the server would refuse. */
    protected override void ValidateFamily(ContentIssues issues)
    {
        int cap = Type switch { ActivityType.Raid => 6, ActivityType.Crucible => 6, ActivityType.Gambit => 4, ActivityType.Patrol or ActivityType.PublicEvent => 9, _ => 3 };
        if (FireteamMin < 1 || FireteamMin > FireteamMax) issues.Error("fireteam_min", "Must be 1..fireteam_max.");
        if (FireteamMax > cap) issues.Error("fireteam_max", $"{Type} allows at most {cap} players per fireteam.");
        if (RecommendedPower is < 0 or > 2000) issues.Error("recommended_power", "Must be 0..2000 (vision.md Power cap).");
        if (MapTag is not null && ParseHash(MapTag) is null) issues.Error("map_tag", "Not a tag.");
        if (MissionTag is not null && ParseHash(MissionTag) is null) issues.Error("mission_tag", "Not a tag.");
        var ids = new Dictionary<string, ActivityObjective>(StringComparer.Ordinal);
        for (int i = 0; i < Objectives.Count; i++)
        {
            var objective = Objectives[i];
            if (!IsValidId(objective.Id)) issues.Error($"objectives[{i}].id", "Objective ids are a-z, 0-9, '_', '-' or '.'.");
            else if (!ids.TryAdd(objective.Id, objective)) issues.Error($"objectives[{i}].id", "Repeated objective id.");
            if (objective.Count < 1) issues.Error($"objectives[{i}].count", "At least 1.");
            if (objective.TimerSeconds < 0) issues.Error($"objectives[{i}].timer_seconds", "Cannot be negative.");
            for (int e = 0; e < objective.Encounters.Count; e++)
                if (objective.Encounters[e].Count is < 1 or > 200) issues.Error($"objectives[{i}].encounters[{e}].count", "Must be 1..200.");
        }
        if (Objectives.Count == 0) { issues.Error("objectives", "An activity needs at least one objective."); return; }
        for (int i = 0; i < Objectives.Count; i++)
            foreach (string next in Objectives[i].Next)
                if (!ids.ContainsKey(next)) issues.Error($"objectives[{i}].next", $"No objective '{next}'.");
        if (!ids.ContainsKey(Start)) { issues.Error("start", "Name the first objective."); return; }
        // Reachability and completion from the start objective.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([Start]);
        bool completes = false;
        while (queue.Count > 0)
        {
            string id = queue.Dequeue();
            if (!seen.Add(id) || !ids.TryGetValue(id, out var objective)) continue;
            if (objective.Next.Count == 0) completes = true;
            foreach (string next in objective.Next) queue.Enqueue(next);
        }
        foreach (var objective in Objectives.Where(objective => !seen.Contains(objective.Id)))
            issues.Error("objectives", $"'{objective.Id}' is unreachable from '{Start}'.");
        if (!completes) issues.Error("objectives", "No reachable objective completes the activity (one with no next).");
        if (Objectives.Count > 1 && !Objectives.Any(objective => objective.Checkpoint) && Type is ActivityType.Raid or ActivityType.Dungeon)
            issues.Warn("objectives", "Raids and dungeons usually set checkpoints.");
        foreach (var (hash, quantity) in Rewards.Items)
        {
            if (ParseHash(hash) is null) issues.Error("rewards.items." + hash, "Not an item hash.");
            if (quantity is < 1 or > 1000000) issues.Error("rewards.items." + hash, "Quantity 1..1000000 (grant_reward's limit).");
        }
    }

    /** Content this activity points at. */
    public override IEnumerable<ContentReference> References()
    {
        for (int i = 0; i < Objectives.Count; i++)
            for (int e = 0; e < Objectives[i].Encounters.Count; e++)
                yield return new(ContentFamilies.Enemy, Objectives[i].Encounters[e].Enemy, $"objectives[{i}].encounters[{e}].enemy");
        for (int i = 0; i < Rewards.LootPools.Count; i++)
            if (!LootPoolDefinition.BuiltInPools.Contains(Rewards.LootPools[i]))
                yield return new(ContentFamilies.LootPool, Rewards.LootPools[i], $"rewards.loot_pools[{i}]");
    }
}

/** One perk of a seasonal artifact column. */
public sealed class ArtifactPerk
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? PlugHash { get; set; }
    public List<int> SandboxPerks { get; set; } = [];
}

/** One column of artifact perks. */
public sealed class ArtifactColumn
{
    /** Perks unlocked in earlier columns before this column opens. */
    public int UnlockAfter { get; set; }
    public List<ArtifactPerk> Perks { get; set; } = [];
}

/** A seasonal artifact (Studio.md Phase 38, master.md seasonal artifact): columns of mods with unlock thresholds. */
public sealed class ArtifactDefinition : ContentDocument
{
    [JsonIgnore] public override string Family => ContentFamilies.Artifact;
    public int MaxUnlocks { get; set; } = 12;
    public List<ArtifactColumn> Columns { get; set; } = [];

    /** Checks this artifact for problems the server would refuse. */
    protected override void ValidateFamily(ContentIssues issues)
    {
        if (Columns.Count is < 1 or > 5) issues.Error("columns", "An artifact has 1..5 columns.");
        int total = Columns.Sum(column => column.Perks.Count);
        if (MaxUnlocks < 1 || MaxUnlocks > total) issues.Error("max_unlocks", $"Must be 1..{Math.Max(total, 1)} (the perks on the artifact).");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int previous = -1;
        for (int c = 0; c < Columns.Count; c++)
        {
            var column = Columns[c];
            if (c == 0 && column.UnlockAfter != 0) issues.Error("columns[0].unlock_after", "The first column is open from the start.");
            if (column.UnlockAfter < previous) issues.Error($"columns[{c}].unlock_after", "Thresholds must not decrease.");
            if (column.UnlockAfter > MaxUnlocks) issues.Error($"columns[{c}].unlock_after", "The column can never open.");
            previous = column.UnlockAfter;
            if (column.Perks.Count is < 1 or > 5) issues.Error($"columns[{c}].perks", "A column holds 1..5 perks.");
            for (int p = 0; p < column.Perks.Count; p++)
            {
                var perk = column.Perks[p];
                if (!IsValidId(perk.Id)) issues.Error($"columns[{c}].perks[{p}].id", "Perk ids are a-z, 0-9, '_', '-' or '.'.");
                else if (!ids.Add(perk.Id)) issues.Error($"columns[{c}].perks[{p}].id", "Repeated perk id.");
                if (string.IsNullOrWhiteSpace(perk.Name)) issues.Error($"columns[{c}].perks[{p}].name", "Name cannot be empty.");
                if (perk.PlugHash is not null && ParseHash(perk.PlugHash) is null) issues.Error($"columns[{c}].perks[{p}].plug_hash", "Not a hash.");
                if (perk.PlugHash is null) issues.Warn($"columns[{c}].perks[{p}].plug_hash", "No plug: the server cannot sell this perk.");
                foreach (int index in perk.SandboxPerks)
                    if (index is < 0 or > ushort.MaxValue) issues.Error($"columns[{c}].perks[{p}].sandbox_perks", $"Perk index {index} is out of range.");
                if (perk.SandboxPerks.Count > 8) issues.Error($"columns[{c}].perks[{p}].sandbox_perks", "At most 8 sandbox perks per mod.");
            }
        }
    }
}
