using System.Text.Json.Nodes;
using Solforge.Weapons;
using SunriseStudio.Formats;

// Phase 20 check: a sample recipe round-trips through the .weapon.json schema and validation, every
// Parhelion rule ported so far rejects a broken copy, and donor checks run against a hand-built donor.
// Needs no game install.
int failures = 0;
void Check(bool condition, string label)
{
    Console.WriteLine($"{(condition ? "ok" : "FAIL")}: {label}");
    if (!condition) failures++;
}
string sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sample.weapon.json"));

var recipe = WeaponRecipeJson.Parse(sample);
string saved = WeaponRecipeJson.Serialize(recipe);
string resaved = WeaponRecipeJson.Serialize(WeaponRecipeJson.Parse(saved));
Check(saved == resaved, "sample round-trips byte for byte after the first save");
Check(JsonNode.DeepEquals(JsonNode.Parse(saved), JsonNode.Parse(resaved)), "saved JSON is structurally stable");
Check(recipe.Overrides.InvestmentStats.Select(stat => stat.DefinitionIndex).SequenceEqual(new ushort[] { 16, 22 }), "stats load in canonical order");
Check(recipe.Overrides.SocketColumns.Count == 3 && recipe.Overrides.SocketColumns[0] is null, "kept socket lanes stay null");
Check(recipe.Overrides.Tuning?.ToTuning() is { RoundsPerMinute: 140, Magazine: 9 }, "tuning maps onto Studio's WeaponTuning");
Check(WeaponNamespace.ForWeaponName("First Light!") == "solforge.first-light", "namespace slug matches the sample");
Check(WeaponRecipeValidator.TryParsePatchBytes("0x00 ff_10", out var patchBytes) && patchBytes is [0x00, 0xFF, 0x10], "patch bytes ignore spaces and underscores");

// Each mutation must be rejected with the field it names.
(string Label, Action<JsonObject> Mutate, string Expect)[] broken =
[
    ("schema 2", root => root["schema"] = 2, "Unsupported recipe schema 2"),
    ("unknown field", root => root["badge"] = "x", "badge"),
    ("missing required name", root => root.Remove("name"), "name"),
    ("lower-case hash", root => root["donor"]!["item_hash"] = "0x3e7b47f8", "donor.item_hash"),
    ("reserved donor hash", root => root["donor"]!["item_hash"] = "0x811C9DC5", "reserved empty-name hash"),
    ("bad namespace", root => root["namespace"] = "weapons.first", "must begin with"),
    ("empty name", root => root["name"] = "  ", "Weapon name cannot be empty"),
    ("duplicate identity", root => root["identity"]!["unlock_hash"] = "0xA1B2C3D4", "identity.unlock_hash repeats"),
    ("locale 13", root => root["locale_overrides"]![0]!["locale_index"] = 13, "outside 0..12"),
    ("locale without primary", root => root["locale_overrides"]![0]!["type_name"] = "Canon", "does not author that optional text field"),
    ("duplicate stat", root => root["overrides"]!["investment_stats"]!.AsArray().Add(new JsonObject { ["definition_index"] = 16, ["value"] = 1 }), "overridden more than once"),
    ("removed and overridden", root => root["overrides"]!["removed_investment_stats"] = new JsonArray(22), "both overridden and removed"),
    ("both power caps", root => root["overrides"]!["power_cap_groups"] = new JsonArray(1, 2), "not both"),
    ("pattern sentinel", root => root["overrides"]!["weapon_pattern_index"] = 65535, "disabled sentinel"),
    ("bad rarity", root => root["overrides"]!["rarity"] = "mythic", "rarity"),
    ("duplicate plug", root => root["overrides"]!["socket_columns"]![2]!["choices"] = new JsonArray("0x9FBF6A53", "0x9FBF6A53"), "duplicate plug 0x9FBF6A53"),
    ("weights mismatch", root => root["overrides"]!["socket_columns"]![2]!["choice_weight_bits"] = new JsonArray(1065353216u, 0u), "2 weights for 1 choices"),
    ("removed socket keeps settings", root => root["overrides"]!["socket_columns"]![2] = new JsonObject
        { ["choices"] = new JsonArray(), ["socket_type"] = 65535, ["reusable_plug_set_index"] = 4 }, "cannot retain plug selection settings"),
    ("odd patch bytes", root => root["overrides"]!["raw_payload_patches"]![0]!["bytes"] = "0x000", "even number of hexadecimal digits"),
    ("overlapping patches", root => root["overrides"]!["raw_payload_patches"]!.AsArray().Add(new JsonObject
        { ["target"] = "item_stats_block", ["offset"] = 18, ["bytes"] = "ff" }), "Raw payload patches 0 and 1 overlap"),
    ("mixed patch spaces", root => root["overrides"]!["raw_payload_patches"]!.AsArray().Add(new JsonObject
        { ["target"] = "item_definition", ["offset"] = 0, ["bytes"] = "ff" }), "cannot be mixed"),
    ("zero magazine", root => root["overrides"]!["tuning"]!["magazine"] = 0, "Magazine size must be positive"),
    ("item hash is the donor's", root => root["identity"]!["item_hash"] = "0x3E7B47F8", "The authored item hash collides with its donor"),
    ("localized hash in the donor bank", root => root["identity"]!["name_hash"] = "0x00001000", "Localized hashes must sort after"),
    ("authored hash repeats a derived one", root => root["identity"]!["source_hash"] =
        WeaponIdentity.FromNamespace("solforge.first-light").TypeHash!.Value.Text, "identity.source_hash repeats identity.type_hash"),
    ("randomized plug set without a program", root => root["overrides"]!["socket_columns"]![2]!["randomized_plug_set_index"] = 3,
        "randomized selection program cannot be empty"),
];
foreach (var (label, mutate, expect) in broken)
{
    var root = JsonNode.Parse(sample)!.AsObject();
    mutate(root);
    string? message = null;
    try { WeaponRecipeJson.Parse(root.ToJsonString()); }
    catch (WeaponRecipeException error) { message = error.Message; }
    Check(message?.Contains(expect, StringComparison.Ordinal) == true, $"rejects {label}{(message is null ? " (accepted)" : $" ({message.Split('\n')[0].Trim()})")}");
}

// Parhelion accepts an empty per-choice condition (the choice is unconditional).
var conditioned = JsonNode.Parse(sample)!.AsObject();
conditioned["overrides"]!["socket_columns"]![2]!["choice_conditions"] = new JsonArray(new JsonArray());
Check(WeaponRecipeValidator.Validate(WeaponRecipeJson.Parse(conditioned.ToJsonString())).Count == 0, "accepts an empty choice condition");

// Identity hashes derive exactly as Parhelion's from_namespace (values from its good-company recipe).
var goodCompany = WeaponIdentity.FromNamespace("parhelion.good-company");
Check(goodCompany is { ItemHash.Text: "0x45517CF9", UnlockHash.Text: "0xB92B6460", SourceHash.Text: "0x78CC2ABB" }
    && goodCompany.CollectionRequirementHash?.Text == "0x7A93FAA4", "namespace derivation matches Parhelion");
var resolved = recipe.Identity.Resolve(recipe.Namespace).ToList();
Check(resolved.Count == 12 && resolved.Single(entry => entry.Field == "identity.type_hash").Derived, "omitted optional hashes are derived");

// Collections node budget: stock pages are free, added pages and badges count.
var glaives = new WeaponCollectionDestination { Ammo = RecipeAmmoType.Special, Family = WeaponFamily.Glaives };
var budget = WeaponCollectionNodeBudget.ForRecipes([recipe, recipe with { Overrides = recipe.Overrides with { CollectionDestination = glaives } }]);
Check(budget.Pages == 1 && budget.Used == WeaponCollections.BaseNodeCount + 1 && budget.Problem is null, "node budget counts only added pages");
Check((budget with { Badges = 30 }).Problem?.Contains("Collections needs 1049 of 1024 nodes") == true, "node budget rejects an overfull build");
Check(WeaponCollections.Template(WeaponFamily.Glaives) is { Ammo: RecipeAmmoType.Special, Family: WeaponFamily.FusionRifles }, "glaives reuse the fusion rifle page");

// FromInvestment offers every installed stat the donor lacks.
var detail = new InvestmentItemDetail(new InvestmentItem(0, 0x3E7B47F8, 0, 0, "Stock hand cannon", "Hand Cannon", "", 0, 5, 1, true),
    [new InvestmentStat(16, "Stability", 40, new AssetProperty("stability", "40"))], [], [], [], []);
var investmentDonor = WeaponDonorProfile.FromInvestment(detail, installedStats: [new(16, "Stability"), new(22, "Handling")]);
Check(investmentDonor.AddableStats is [{ DefinitionIndex: 22 }], "FromInvestment fills addable stats");
Check(!WeaponRecipeValidator.ValidateAgainstDonor(recipe, investmentDonor).Any(issue => issue.Message.Contains("neither on the donor")),
    "an installed stat the donor lacks can be added");

// Donor checks against a hand-built profile shaped like Studio's investment detail.
var donor = new WeaponDonorProfile("Stock hand cannon", 0x3E7B47F8, RecipeInventorySlot.Kinetic,
    [
        new(0, 10, WeaponRecipe.MaxSocketChoices, null),
        new(1, 11, WeaponRecipe.MaxSocketChoices, new HashSet<uint> { 0x5778AFAD, 0x6DAFD49E }),
        new(2, 92, WeaponRecipe.MaxSocketChoices, new HashSet<uint> { 0x9FBF6A53 }),
    ],
    [new(16, "Stability", 40, 0, 100), new(22, "Handling", 50, 0, 100)]);
Check(WeaponRecipeValidator.ValidateAgainstDonor(recipe, donor).Count == 0, "sample fits its donor");
var narrow = donor with
{
    Sockets = [donor.Sockets[0], donor.Sockets[1] with { SupportedPlugs = new HashSet<uint> { 0x5778AFAD } }],
    Stats = [new(16, "Stability", 40, 0, 50)],
};
var donorIssues = WeaponRecipeValidator.ValidateAgainstDonor(recipe, narrow).Select(issue => issue.Message).ToList();
Check(donorIssues.Any(message => message.Contains("plug 0x6DAFD49E is not in the donor socket's plug set")), "flags a plug outside the donor plug set");
Check(donorIssues.Any(message => message.Contains("column 2 is outside the donor's 2 sockets")), "flags a lane past the donor sockets");
Check(donorIssues.Any(message => message.Contains("Stability value 55 is outside")), "flags a stat outside its curve");
Check(donorIssues.Any(message => message.Contains("definition 22 is neither on the donor")), "flags an unknown stat");
var catalog = donor with
{
    PlugSetCount = 2, KnownSocketTypes = new HashSet<ushort> { 10, 11 },
    WeaponPatternIndices = new HashSet<ushort>(), StatGroupIndices = new HashSet<ushort>(),
};
var catalogRecipe = recipe with { Overrides = recipe.Overrides with { WeaponPatternIndex = 7, StatGroupIndex = 9,
    SocketColumns = [null, recipe.Overrides.SocketColumns[1]! with { ReusablePlugSetIndex = 2 }, recipe.Overrides.SocketColumns[2]] } };
var catalogIssues = WeaponRecipeValidator.ValidateAgainstDonor(catalogRecipe, catalog).Select(issue => issue.Message).ToList();
Check(catalogIssues.Any(message => message.Contains("Socket 2 uses unknown native socket type 92")), "flags an unknown socket type");
Check(catalogIssues.Any(message => message.Contains("reusable plug-set index 2, but the installed table contains 2 rows")), "flags a plug set past the table");
Check(catalogIssues.Any(message => message.Contains("Weapon-pattern index 7")) && catalogIssues.Any(message => message.Contains("Stat-display group 9")),
    "flags pattern and stat-group indices no stock weapon uses");

Console.WriteLine(failures == 0 ? "Solforge.Weapons checks passed." : $"{failures} Solforge.Weapons check(s) failed.");
return failures == 0 ? 0 : 1;
