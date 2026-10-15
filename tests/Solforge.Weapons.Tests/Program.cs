using System.Text.Json.Nodes;
using Solforge.Weapons;

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

Console.WriteLine(failures == 0 ? "Solforge.Weapons checks passed." : $"{failures} Solforge.Weapons check(s) failed.");
return failures == 0 ? 0 : 1;
