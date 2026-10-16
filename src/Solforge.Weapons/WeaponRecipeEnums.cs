namespace Solforge.Weapons;

/** Recipe enums. JSON spells every member in snake_case (WeaponRecipeJson), matching Parhelion's serde names. */
public enum RecipeDamageType { Kinetic, Arc, Solar, Void }
/** Inventory slot the weapon equips into. */
public enum RecipeInventorySlot { Kinetic, Energy, Power }
/** Ammo type the weapon draws from. */
public enum RecipeAmmoType { Primary, Special, Heavy }
/** Item rarity tier. */
public enum RecipeRarity { Common, Uncommon, Rare, Legendary, Exotic }
/** Where the finished weapon appears in Collections. Parhelion's only placement is its Sunrise badge. */
public enum RecipeCollectionPlacement { SunriseBadge }

/** The Collections family node a weapon is filed under. */
public enum WeaponFamily
{
    AutoRifles, Bows, FusionRifles, Glaives, GrenadeLaunchers, HandCannons, LinearFusionRifles, MachineGuns,
    PulseRifles, RocketLaunchers, ScoutRifles, Shotguns, Sidearms, SniperRifles, SubmachineGuns, Swords,
}

/** The record a raw byte patch addresses. ItemDefinition is the whole gameplay definition; every other
 *  item-block target is a named sub-block of it, so the two coordinate spaces cannot be mixed in one recipe. */
public enum RecipeRawPayloadTarget
{
    ItemDefinition, ItemActionBlock, ItemEquippingBlock, ItemFinisherBlock, ItemGearsetBlock, ItemLoreBlock,
    ItemObjectiveBlock, ItemMetricBlock, ItemPlugBlock, ItemQualityBlock, ItemRecordBlock, ItemSackBlock, ItemSetBlock,
    ItemSocketsBlock, ItemStatsBlock, ItemSummaryBlock, ItemTalentGridBlock, ItemTranslationBlock, ItemUnlockBlock,
    ItemValueBlock, ItemInventoryBlock, ItemTraitsDescriptor, ItemTraitRows, ItemStringDefinition, ItemDefinitionIndexRow,
    ItemStringIndexRow, ItemIconRow, DenseIconTagRow, DenseIconSelectorRow, DensePresentationRow, ItemMetadataRow,
    ItemMetadataIndexRow, SandboxPatternRow, SandboxPatternIndexRow, RuntimeWeaponEntity, CollectibleDefinitionRow,
    CollectibleDisplayRow, UnlockDefinitionRow, UnlockSortedIndexRow, UnlockBankRow, UnlockDisplayRow,
}

/** Helpers for classifying raw payload targets. */
public static class RecipeRawPayloadTargets
{
    /** True for the named sub-blocks of the gameplay definition (Parhelion's ITEM_DEFINITION_RAW_SUBTARGETS). */
    public static bool IsItemDefinitionSubBlock(this RecipeRawPayloadTarget target) =>
        target is >= RecipeRawPayloadTarget.ItemActionBlock and <= RecipeRawPayloadTarget.ItemTraitRows;
}
