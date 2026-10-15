namespace Solforge.Weapons;

/** Parhelion's collection.rs: which Collections pages are stock, the stock page a new family copies, and the
 *  shared presentation-node budget every build must fit. */
public static class WeaponCollections
{
    /** Native presentation-node capacity. */
    public const int NodeCapacity = 1024;
    /** Stock presentation nodes (Parhelion's STOCK_PRESENTATION_NODE_COUNT, 924) plus the 4 Parhelion always adds. */
    public const int BaseNodeCount = 924 + 4;

    /** The audited stock item that anchors a stock page, or null when the page is one a build must add. */
    public static uint? StockExemplar(WeaponCollectionDestination destination) => (destination.Ammo, destination.Family) switch
    {
        (RecipeAmmoType.Primary, WeaponFamily.AutoRifles) => 0x61FFEE96,
        (RecipeAmmoType.Primary, WeaponFamily.Bows) => 0x2AEFB232,
        (RecipeAmmoType.Primary, WeaponFamily.HandCannons) => 0x3E7B47F8,
        (RecipeAmmoType.Primary, WeaponFamily.PulseRifles) => 0x14373AFC,
        (RecipeAmmoType.Primary, WeaponFamily.ScoutRifles) => 0x74D68F77,
        (RecipeAmmoType.Primary, WeaponFamily.Sidearms) => 0x5B6757E0,
        (RecipeAmmoType.Primary, WeaponFamily.SubmachineGuns) => 0x478CDF8F,
        (RecipeAmmoType.Special, WeaponFamily.FusionRifles) => 0xCD5D35CD,
        (RecipeAmmoType.Special, WeaponFamily.GrenadeLaunchers) => 0x763740D0,
        (RecipeAmmoType.Special, WeaponFamily.Shotguns) => 0x2B946BA9,
        (RecipeAmmoType.Special, WeaponFamily.SniperRifles) => 0xC56A395D,
        (RecipeAmmoType.Heavy, WeaponFamily.GrenadeLaunchers) => 0x81407A43,
        (RecipeAmmoType.Heavy, WeaponFamily.LinearFusionRifles) => 0x9527F0F7,
        (RecipeAmmoType.Heavy, WeaponFamily.MachineGuns) => 0x23F4BF01,
        (RecipeAmmoType.Heavy, WeaponFamily.RocketLaunchers) => 0x0837DFF1,
        (RecipeAmmoType.Heavy, WeaponFamily.Swords) => 0xF8D186CA,
        _ => null,
    };

    /** The stock page whose structure a family's page reuses. Glaives borrow Fusion Rifles' page. */
    public static WeaponCollectionDestination Template(WeaponFamily family)
    {
        if (family == WeaponFamily.Glaives) return Template(WeaponFamily.FusionRifles);
        var ammo = family switch
        {
            WeaponFamily.FusionRifles or WeaponFamily.GrenadeLaunchers or WeaponFamily.Shotguns or WeaponFamily.SniperRifles => RecipeAmmoType.Special,
            WeaponFamily.LinearFusionRifles or WeaponFamily.MachineGuns or WeaponFamily.RocketLaunchers or WeaponFamily.Swords => RecipeAmmoType.Heavy,
            _ => RecipeAmmoType.Primary,
        };
        return new() { Ammo = ammo, Family = family };
    }
}

/** Parhelion's NodeBudget: nodes one build adds to Collections. Each custom badge takes 4 nodes, each added
 *  (non-stock) weapon page 1 and each gear page 1. Solforge recipes carry no badge, so Badges is the caller's. */
public sealed record WeaponCollectionNodeBudget(int Badges, int Pages, int GearPages = 0)
{
    /** Counts the distinct non-stock pages the recipes of one build file their weapons under. */
    public static WeaponCollectionNodeBudget ForRecipes(IEnumerable<WeaponRecipe> recipes, int badges = 0, int gearPages = 0) =>
        new(badges, recipes.Select(recipe => recipe.Overrides.CollectionDestination)
            .OfType<WeaponCollectionDestination>()
            .Where(destination => WeaponCollections.StockExemplar(destination) is null)
            .Distinct().Count(), gearPages);

    public int Used => WeaponCollections.BaseNodeCount + Badges * 4 + Pages + GearPages;

    /** Null when the build fits, otherwise Parhelion's message. */
    public string? Problem => Used > WeaponCollections.NodeCapacity
        ? $"Collections needs {Used} of {WeaponCollections.NodeCapacity} nodes. Remove a custom badge or an added page from this build."
        : null;
}
