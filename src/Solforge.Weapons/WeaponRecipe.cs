using SunriseStudio.Authoring;

namespace Solforge.Weapons;

/** A weapon recipe (.weapon.json): a stock donor weapon plus everything the new weapon changes. This is the
 *  weapon-making part of Parhelion's WeaponRecipe rewritten in C#; presentation, icon, dye, lore, subclass and
 *  sandbox-perk fields are not part of Solforge's schema (docs/PARHELION_AUDIT.md). Read and write it through
 *  WeaponRecipeJson, which checks the schema version and runs WeaponRecipeValidator. */
public sealed record WeaponRecipe
{
    public const int CurrentSchema = 1;
    /** Most choices one authored socket column may carry (the native 16-bit count). */
    public const int MaxSocketChoices = ushort.MaxValue;
    /** Localized string banks a locale override may target (0..12). */
    public const int LocaleCount = (0x4C - 0x18) / 4;
    /** The hash of the empty name; never valid as an authored or donor item hash. */
    public const uint ReservedEmptyNameHash = 0x811C9DC5;

    public required int Schema { get; init; }
    public required string Namespace { get; init; }
    public required RecipeCollectionPlacement CollectionPlacement { get; init; }
    public required WeaponDonorReference Donor { get; init; }
    /** Optional stock weapon whose geometry the new weapon wears instead of the gameplay donor's. */
    public WeaponDonorReference? PresentationDonor { get; init; }
    public required WeaponIdentity Identity { get; init; }
    public required string Name { get; init; }
    public string? TypeName { get; init; }
    public required string Flavor { get; init; }
    public required string Source { get; init; }
    public string? CollectionName { get; init; }
    public string? CollectionDescription { get; init; }
    public string? InventoryHint { get; init; }
    public string? CollectionRequirement { get; init; }
    public IReadOnlyList<WeaponLocaleText> LocaleOverrides { get; init; } = [];
    public required WeaponRecipeOverrides Overrides { get; init; }

    /** The same recipe with stats, removed stats and locales in their saved order, so reordering in an editor
     *  is not mistaken for a change (Parhelion's canonicalize_investment_stats). */
    public WeaponRecipe Canonical() => this with
    {
        LocaleOverrides = [.. LocaleOverrides.OrderBy(locale => locale.LocaleIndex)],
        Overrides = Overrides with
        {
            InvestmentStats = [.. Overrides.InvestmentStats.OrderBy(stat => stat.DefinitionIndex)],
            RemovedInvestmentStats = [.. Overrides.RemovedInvestmentStats.Order()],
        },
    };
}

/** A stock weapon by item hash. ExpectedName guards against a hash that now names a different item. */
public sealed record WeaponDonorReference
{
    public required HexHash ItemHash { get; init; }
    public string? ExpectedName { get; init; }
}

/** The hashes the new weapon owns. FromNamespace derives all twelve the way Parhelion does; a recipe must
 *  write the required ones, and an omitted optional one is derived from the namespace (Resolve). */
public sealed record WeaponIdentity
{
    public required HexHash ItemHash { get; init; }
    public required HexHash CollectibleHash { get; init; }
    public required HexHash UnlockHash { get; init; }
    public HexHash? PatternGlobalIdHash { get; init; }
    public required HexHash NameHash { get; init; }
    public HexHash? TypeHash { get; init; }
    public required HexHash FlavorHash { get; init; }
    public required HexHash SourceHash { get; init; }
    public HexHash? CollectionNameHash { get; init; }
    public HexHash? CollectionDescriptionHash { get; init; }
    public HexHash? InventoryHintHash { get; init; }
    public HexHash? CollectionRequirementHash { get; init; }

    /** Parhelion's from_namespace as a full identity with every optional hash written. */
    public static WeaponIdentity FromNamespace(string ns)
    {
        var h = WeaponIdentityHashes.Derive(ns).Select(HexHash.From).ToArray();
        return new()
        {
            ItemHash = h[0], CollectibleHash = h[1], UnlockHash = h[2], PatternGlobalIdHash = h[3], NameHash = h[4],
            TypeHash = h[5], FlavorHash = h[6], SourceHash = h[7], CollectionNameHash = h[8],
            CollectionDescriptionHash = h[9], InventoryHintHash = h[10], CollectionRequirementHash = h[11],
        };
    }

    /** All twelve hashes in WeaponIdentityHashes.Roles order as Parhelion's to_compiler sees them: an omitted
     *  optional hash is derived from the namespace and marked Derived. With a malformed namespace those are skipped. */
    public IEnumerable<(int Role, string Field, HexHash Hash, bool Derived)> Resolve(string? ns)
    {
        uint[]? derived = ns is not null && WeaponNamespace.Problem(ns) is null ? WeaponIdentityHashes.Derive(ns) : null;
        HexHash?[] authored = [ItemHash, CollectibleHash, UnlockHash, PatternGlobalIdHash, NameHash, TypeHash, FlavorHash,
            SourceHash, CollectionNameHash, CollectionDescriptionHash, InventoryHintHash, CollectionRequirementHash];
        for (int i = 0; i < authored.Length; i++)
        {
            if (authored[i] is { } hash) yield return (i, WeaponIdentityHashes.Fields[i], hash, false);
            else if (derived is not null) yield return (i, WeaponIdentityHashes.Fields[i], HexHash.From(derived[i]), true);
        }
    }
}

/** Text for one locale bank. A field left null keeps the primary text. */
public sealed record WeaponLocaleText
{
    public required byte LocaleIndex { get; init; }
    public string? Name { get; init; }
    public string? TypeName { get; init; }
    public string? Flavor { get; init; }
    public string? Source { get; init; }
    public string? CollectionName { get; init; }
    public string? CollectionDescription { get; init; }
    public string? InventoryHint { get; init; }
    public string? CollectionRequirement { get; init; }
}

/** Everything the new weapon changes on its donor. Null and empty mean "keep the donor's". */
public sealed record WeaponRecipeOverrides
{
    public IReadOnlyList<WeaponStatOverride> InvestmentStats { get; init; } = [];
    public IReadOnlyList<ushort> RemovedInvestmentStats { get; init; } = [];
    public uint? MaxStackSize { get; init; }
    public RecipeInventorySlot? InventorySlot { get; init; }
    public RecipeAmmoType? AmmoType { get; init; }
    public RecipeDamageType? ModernDamageType { get; init; }
    /** One power-cap group for every version row, or PowerCapGroups for one per row; never both. */
    public ushort? PowerCapGroup { get; init; }
    public IReadOnlyList<ushort>? PowerCapGroups { get; init; }
    public RecipeRarity? Rarity { get; init; }
    public ushort? WeaponPatternIndex { get; init; }
    public ushort? StatGroupIndex { get; init; }
    public WeaponCollectionDestination? CollectionDestination { get; init; }
    /** Solforge addition: fire rate, damage and magazine, applied through Studio's WeaponTuningAuthoring. */
    public WeaponTuningRecipe? Tuning { get; init; }
    /** One entry per donor socket in order; null keeps that socket as the donor has it. */
    public IReadOnlyList<WeaponSocketColumnRecipe?> SocketColumns { get; init; } = [];
    /** Final byte patches for fields the schema does not name yet. */
    public IReadOnlyList<WeaponRawPayloadPatchRecipe> RawPayloadPatches { get; init; } = [];
}

/** Overrides one stat, by definition index, with a fixed value. */
public sealed record WeaponStatOverride
{
    public required ushort DefinitionIndex { get; init; }
    public required int Value { get; init; }
}

/** Collections node (ammo type and weapon family) the finished weapon is filed under. */
public sealed record WeaponCollectionDestination
{
    public required RecipeAmmoType Ammo { get; init; }
    public required WeaponFamily Family { get; init; }
}

/** The tuning a recipe asks for; null keeps the donor's value. */
public sealed record WeaponTuningRecipe
{
    public int? RoundsPerMinute { get; init; }
    public float? DamagePerShot { get; init; }
    public int? Magazine { get; init; }

    /** Converts to the tuning value used by the builder. */
    public WeaponTuning ToTuning() => new(RoundsPerMinute, DamagePerShot, Magazine);
    /** Captures a tuning value as a recipe. */
    public static WeaponTuningRecipe From(WeaponTuning tuning) =>
        new() { RoundsPerMinute = tuning.RoundsPerMinute, DamagePerShot = tuning.DamagePerShot, Magazine = tuning.Magazine };
}

/** The plugs one donor socket offers. A column with no choices and SocketType 65535 removes the socket. */
public sealed record WeaponSocketColumnRecipe
{
    public required IReadOnlyList<HexHash> Choices { get; init; }
    public ushort? SocketType { get; init; }
    /** f32 bit patterns, one per choice, or empty for equal weights. */
    public IReadOnlyList<uint> ChoiceWeightBits { get; init; } = [];
    /** One RPN program per choice, or empty for unconditional choices. */
    public IReadOnlyList<IReadOnlyList<WeaponNumericInstruction>> ChoiceConditions { get; init; } = [];
    public ushort? ReusablePlugSetIndex { get; init; }
    public ushort? RandomizedPlugSetIndex { get; init; }
    public IReadOnlyList<WeaponNumericInstruction> RandomizedSelectionProgram { get; init; } = [];
}

/** One instruction of a plug-condition or selection program: opcode plus operand. */
public sealed record WeaponNumericInstruction
{
    public required byte Opcode { get; init; }
    public required ushort Operand { get; init; }
}

/** Hex bytes written at Offset inside Target; whitespace, '_' and a leading 0x are ignored. */
public sealed record WeaponRawPayloadPatchRecipe
{
    public required RecipeRawPayloadTarget Target { get; init; }
    public required uint Offset { get; init; }
    public required string Bytes { get; init; }
}
