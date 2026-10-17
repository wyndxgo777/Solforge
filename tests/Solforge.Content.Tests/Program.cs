using System.IO.Compression;
using System.Text.Json.Nodes;
using Solforge.Content;
using Solforge.Content.Packaging;

// Solforge.Content checks: subclass template and build rules (mirroring the Sol server), server-view import,
// subclass_nodes.tsv and loot_pools.lua export, activity reachability, JSON round trip with unknown fields,
// .solmod packing and install snapshots. Needs no game install; temp files stay under one scratch folder.
int failures = 0;
void Check(bool condition, string label)
{
    Console.WriteLine($"{(condition ? "ok" : "FAIL")}: {label}");
    if (!condition) failures++;
}

var subclass = SubclassTemplates.New("stormcaller", "Stormcaller", GuardianClass.Warlock, "arc");
Check(!subclass.Validate().HasErrors, "the shipped-layout template validates: " + string.Join("; ", subclass.Validate()));
Check(!SubclassRules.CheckBuild(subclass, subclass.DefaultBuild!).HasErrors, "the template's default build is valid");
SubclassBuild Build(Action<SubclassBuild> change)
{
    var build = new SubclassBuild { Super = "super-lane", Grenade = "grenade-1", Melee = "t1-melee", ClassAbility = "class-1", Movement = "jump-1", Perks = ["t1-perk-1"] };
    change(build);
    return build;
}
bool Refused(SubclassBuild build, string words) =>
    SubclassRules.CheckBuild(subclass, build).Any(issue => issue.Error && issue.Message.Contains(words, StringComparison.OrdinalIgnoreCase));
Check(Refused(Build(b => b.Perks = ["t1-perk-1", "t1-melee"]), "not a perk"), "a melee node is not a perk");
subclass.Limits.PerTree = 1;
Check(Refused(Build(b => b.Perks = ["t1-perk-1", "t1-perk-2"]), "at most 1"), "per-path limit");
subclass.Limits.PerTree = 2;
Check(Refused(Build(b => { b.Super = "t1-super"; b.Perks = ["t3-perk-2"]; }), "path super replaces"), "a super modifier is refused under a path super");
subclass.Trees[1].Nodes[3].ModifiesSuper = true;
Check(Refused(Build(b => b.Perks = ["t2-perk-2", "t3-perk-2"]), "exclude each other"), "modifiers from two paths exclude each other");
subclass.Trees[1].Nodes[3].ModifiesSuper = false;
Check(Refused(Build(b => b.Grenade = "grenade-9"), "No node"), "unknown node ids are refused");
Check(Refused(Build(b => b.Super = null), "Pick one"), "each ability slot needs one pick");
SubclassTemplates.AddAbilities3(subclass);
Check(!SubclassRules.CheckBuild(subclass, Build(b => { b.Aspects = ["aspect-1"]; b.Fragments = ["fragment-1", "fragment-2"]; })).HasErrors,
    "two fragments fit a two-slot aspect");
Check(Refused(Build(b => { b.Aspects = ["aspect-2"]; b.Fragments = ["fragment-1", "fragment-2"]; }), "slots"), "fragments are capped by aspect slots");
subclass.Limits.Aspects = 1;
Check(Refused(Build(b => b.Aspects = ["aspect-1", "aspect-2"]), "At most 1 aspects"), "aspect limit");
subclass.Limits.Aspects = 2;
Check(SubclassRules.CheckBuild(subclass, Build(b => b.Perks = ["t1-perk-1", "t1-perk-2", "t2-perk-1", "t2-perk-2", "t3-perk-1"]))
    .Any(issue => !issue.Error && issue.Message.Contains("server", StringComparison.OrdinalIgnoreCase)), "a fifth perk warns about today's server");
Check(SubclassLayout.SlotOf(10) == "super" && SubclassLayout.SlotOf(16).StartsWith("tree", StringComparison.Ordinal), "entry slots follow the server layout");

// The Sol server's GET /api/subclass shape.
var view = JsonNode.Parse("""
{"class":"hunter","subclass":{"name":"Arcstrider","hash":1334959255,"element":"Arc","limits":{"perks":4,"per_tree":2},
 "slots":[{"slot":"super","nodes":[{"entry":10,"name":"Arc Staff","description":"","role":"super"}]},
          {"slot":"grenade","nodes":[{"entry":7,"name":"Flux","description":"","role":"grenade"}]}],
 "trees":[{"index":1,"name":"Way of the Warrior","description":"","nodes":[{"entry":11,"name":"Combat Flow","description":"Dodge","role":"perk","tree":1}]}]}}
""")!;
var imported = SubclassInterop.FromServerView(view);
Check(imported.Id == "arcstrider" && imported.Class == GuardianClass.Hunter && imported.Element == "arc" && imported.ItemHash == "0x4F91DC97",
    $"server view imports: {imported.Id} {imported.Class} {imported.Element} {imported.ItemHash}");
Check(imported.Limits.Perks == 4 && imported.Limits.SelectableNodes == 5 && imported.Trees.Single().Nodes.Single().Entry == 11, "server limits and nodes import");
string tsv = SubclassInterop.NodeNamesTsv([imported]);
Check(tsv.Contains("0x4F91DC97\ttree1\tWay of the Warrior\t\n") && tsv.Contains("0x4F91DC97\t11\tCombat Flow\tDodge\n"), "subclass_nodes.tsv rows: " + tsv);

// Loot pools and the export set.
var pool = (LootPoolDefinition)ContentStore.NewDocument(ContentFamilies.LootPool, "moon_extra", "Moon extras");
pool.Any["0x00000001"] = 2;
pool.Hunter["0x00000002"] = 1;
pool.EnemyKill.Add(new() { Id = "moon_kills", Activity = "patrol", Bubble = "anchor", Chance = .1 });
Check(!pool.Validate().HasErrors, "loot pool validates: " + string.Join("; ", pool.Validate()));
string lua = ContentExport.LootPoolsModule([pool]);
Check(lua.Contains("pools") && lua.Contains("moon_extra") && lua.Contains("[\"0x00000001\"]") && lua.Contains("enemy_kill") && lua.Contains("chance"),
    "loot_pools.lua carries pools and kill rules:\n" + lua);
// get_item_sources reads loot_pools.lua back: the server's decimal-keyed file, then the project's module layered on top.
const string serverPools = """
    -- Loot pools the offline Sunrise server pays.
    return {
        pools = {
        moon_extra = {
            any = {
                ["1"] = 1,
                ["640114618"] = 3,   -- the project layer leaves this one alone
            },
        },
        lost_ghost = { any = { ["443031982"] = 1 }, quantity = 9, profile = true, },
        exotic_weapons = { catalog = "exotic_weapons", },
        old = { only = { "5", 6 } },
        },
        enemy_kill = { ghosts = { pool = "lost_ghost", chance = 0.5, bubble = "anchor" } },
    }
    """;
var serverModule = LootPoolsReader.Parse(serverPools);
Check(serverModule?["pools"]?["lost_ghost"]?["quantity"]?.GetValue<double>() == 9 && serverModule["pools"]!["old"]!["only"] is System.Text.Json.Nodes.JsonArray { Count: 2 },
    "loot_pools.lua parses: " + serverModule?.ToJsonString());
var layered = new[] { (serverModule, "server"), (LootPoolsReader.Parse(lua), "project") };
var ghost = LootPoolsReader.Find(layered, 443031982);
Check(ghost.Count == 1 && ghost[0] is { Pool: "lost_ghost", List: "any", Quantity: 9, Profile: true } && ghost[0].Rules.Single().Contains("bubble~anchor"),
    "a server pool hit carries quantity, profile and its enemy_kill rule");
var moon = LootPoolsReader.Find(layered, 1);
Check(moon.Count == 1 && moon[0] is { Pool: "moon_extra", Weight: 2, File: "project" } && moon[0].Rules.Single().StartsWith("moon_kills"),
    "the project layer overrides a server pool's weight and adds its rule: " + string.Join("; ", moon));
Check(LootPoolsReader.Find(layered, 640114618).Single().Weight == 3 && LootPoolsReader.Find(layered, 2).Single().List == "hunter",
    "unlisted server items survive the project layer; class lists are named");
Check(LootPoolsReader.Find(layered, 6).Single().List == "only" && LootPoolsReader.Find(layered, 7).Count == 0, "only lists match by hash");
Check(LootPoolsReader.Find(layered, 99, name => name == "exotic_weapons").Single().List == "catalog:exotic_weapons"
    && LootPoolsReader.Find(layered, 99).Count == 0, "catalog pools match only when the item is in the catalog");
var removed = LootPoolsReader.Find([(serverModule, "server"), (LootPoolsReader.Parse("return { pools = { moon_extra = { any = { [\"0x00000001\"] = 0 } } } }"), "overlay")], 1);
Check(removed.Count == 0, "weight 0 in a later layer removes the item");
bool badLua = false;
try { LootPoolsReader.Parse("return { pools = os.exit() }"); } catch (InvalidDataException) { badLua = true; }
Check(badLua, "code in the module is refused");
var files = ContentExport.Build([pool, subclass]);
Check(files.Select(file => file.RelativePath).Order(StringComparer.Ordinal)
    .SequenceEqual(["server/data/subclass_nodes.tsv", "server/scripts/loot_pools.lua", "server/scripts/sol_custom_data.lua"]), "export writes the three server files");
Check(ContentExport.CustomDataPath == "server/scripts/sol_custom_data.lua" && files.All(file => !file.RelativePath.Contains("sol_content")),
    "custom data is sol_custom_data.lua, not the server's sol_content gate");

// Custom data the server reads (server/activity/authored_content.cpp): enemy squads and drops, artifact perks.
var grunt = (EnemyDefinition)ContentStore.NewDocument(ContentFamilies.Enemy, "dreg", "Dreg");
grunt.LootPool = "moon";
grunt.LootChance = 0.25;
grunt.Squads = [new() { Squad = "SQ_DREGS*", Counts = [4, 2] }, new() { Squad = "sq_boss", Activity = "strike_bond", CountScale = 2 }];
Check(!grunt.Validate().HasErrors, "enemy squad bindings validate: " + string.Join("; ", grunt.Validate()));
var badGrunt = (EnemyDefinition)ContentStore.NewDocument(ContentFamilies.Enemy, "bad", "Bad");
badGrunt.LootChance = 2;
badGrunt.Squads = [new() { Squad = "" }, new() { Squad = "sq", Counts = [65] }, new() { Squad = "sq", CountScale = 11 }];
var badIssues = badGrunt.Validate();
Check(new[] { "loot_chance", "squads[0].squad", "squads[1].counts", "squads[2].count_scale" }
    .All(field => badIssues.Any(issue => issue.Error && issue.Path == field)), "bad squad bindings are errors: " + string.Join("; ", badIssues));
var poolOnly = (EnemyDefinition)ContentStore.NewDocument(ContentFamilies.Enemy, "pool_only", "Pool only");
poolOnly.LootPool = "moon";
Check(poolOnly.Validate().Any(issue => !issue.Error && issue.Path == "loot_pool"), "a loot_pool without squads warns (drops are per squad)");
var season = (ArtifactDefinition)ContentStore.NewDocument(ContentFamilies.Artifact, "season", "Season");
season.Columns = [new() { Perks = [new() { Id = "p1", Name = "P1", PlugHash = "0x00000011", SandboxPerks = [7] }] }];
season.MaxUnlocks = 1;
Check(!season.Validate().HasErrors, "an artifact perk with sandbox perks validates: " + string.Join("; ", season.Validate()));
season.Columns[0].Perks[0].SandboxPerks = [70000];
Check(season.Validate().Any(issue => issue.Error && issue.Path.EndsWith("sandbox_perks", StringComparison.Ordinal)), "an artifact sandbox perk out of range is an error");
season.Columns[0].Perks[0].SandboxPerks = [7];
string custom = ContentExport.Build([grunt, season]).Single(file => file.RelativePath == ContentExport.CustomDataPath).Text;
Check(custom.Contains("squads = {") && custom.Contains("squad = \"SQ_DREGS*\"") && custom.Contains("count_scale = 2") && custom.Contains("loot_chance = 0.25")
    && custom.Contains("counts = {") && custom.Contains("activity = \"strike_bond\"") && custom.Contains("sandbox_perks = {")
    && custom.Contains("plug_hash = \"0x00000011\"") && custom.Contains("schema = 1"), "sol_custom_data.lua carries what the server reads:\n" + custom);

// Runtime support: Live only where the server applies the whole family; client-owned parts say why.
RuntimeSupport SupportOf(string id) => ContentFamilies.Find(id)!.Support;
Check(SupportOf(ContentFamilies.Artifact) == RuntimeSupport.Live && SupportOf(ContentFamilies.LootPool) == RuntimeSupport.Live
    && SupportOf(ContentFamilies.Ability) == RuntimeSupport.Partial && SupportOf(ContentFamilies.Enemy) == RuntimeSupport.Partial
    && SupportOf(ContentFamilies.Activity) == RuntimeSupport.Partial && SupportOf(ContentFamilies.DamageType) == RuntimeSupport.AuthoringOnly,
    "runtime support levels match the server reader");
Check(ContentFamilies.All.Where(family => family.Support != RuntimeSupport.Live)
        .All(family => family.Notes.Contains("client", StringComparison.OrdinalIgnoreCase) || family.Notes.Contains("authoring only", StringComparison.OrdinalIgnoreCase))
    && ContentFamilies.All.Where(family => family.Id != ContentFamilies.LootPool).All(family => family.Output.Contains("sol_custom_data.lua")),
    "non-live families say what is not applied, and outputs name sol_custom_data.lua");

// Activities: unreachable objectives and missing completions are errors.
var activity = (ActivityDefinition)ContentStore.NewDocument(ContentFamilies.Activity, "strike", "Strike");
activity.Objectives = [new() { Id = "a", Next = ["b"] }, new() { Id = "b" }, new() { Id = "orphan" }];
activity.Start = "a";
Check(activity.Validate().Any(issue => issue.Error && issue.Message.Contains("'orphan' is unreachable")), "unreachable objective is an error");
activity.Objectives[1].Next = ["a"];
activity.Objectives.RemoveAt(2);
Check(activity.Validate().Any(issue => issue.Message.Contains("completes")), "a loop with no end is an error");

string scratch = Path.Combine(Path.GetTempPath(), "solforge-content-tests-" + Guid.NewGuid().ToString("N"));
try
{
    // Store round trip, unknown members and merge patch.
    var store = new ContentStore(scratch);
    store.Save(subclass);
    string path = store.PathOf(ContentFamilies.Subclass, "stormcaller");
    var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    json["future_field"] = new JsonObject { ["kept"] = true };
    File.WriteAllText(path, json.ToJsonString());
    var loaded = (SubclassDefinition)store.Load(ContentFamilies.Subclass, "stormcaller");
    store.Save(loaded);
    Check(JsonNode.Parse(File.ReadAllText(path))?["future_field"]?["kept"]?.GetValue<bool>() == true, "unknown members survive load and save");
    Check(ContentJson.Serialize(loaded) == ContentJson.Serialize(store.Load(ContentFamilies.Subclass, "stormcaller")), "saves are stable");
    var patched = ContentJson.MergePatch(ContentJson.ToNode(loaded), new JsonObject { ["name"] = "Stormbringer", ["description"] = null });
    Check((string?)patched["name"] == "Stormbringer" && !patched.ContainsKey("description") && patched["trees"] is JsonArray, "merge patch sets, removes and keeps");
    var enemy = (EnemyDefinition)ContentStore.NewDocument(ContentFamilies.Enemy, "captain", "Captain");
    enemy.LootPool = "missing_pool";
    store.Save(enemy);
    Check(store.ValidateAll(ContentFamilies.Enemy).Any(row => row.Issue.Message.Contains("missing_pool")), "cross-references to missing documents are reported");
    Check(store.List().Count == 2 && store.Delete(ContentFamilies.Enemy, "captain") && store.List().Count == 1, "list and delete");

    // .solmod: pack, verify, tamper.
    string mods = Path.Combine(scratch, "mods"), mod = Path.Combine(mods, "my_mod");
    Directory.CreateDirectory(Path.Combine(mod, "scripts"));
    File.WriteAllText(Path.Combine(mod, "mod.json"), """{"id":"my_mod","version":"1.0.0","name":"My mod"}""");
    File.WriteAllText(Path.Combine(mod, "scripts", "main.lua"), "return 1");
    string container = Path.Combine(scratch, "my_mod.solmod");
    var manifest = SolmodContainer.Pack(mod, container);
    Check(manifest.Id == "my_mod" && manifest.Files.Count == 2 && SolmodContainer.Verify(container).Count == 0, "a packed .solmod verifies");
    using (var zip = ZipFile.Open(container, ZipArchiveMode.Update))
    {
        var entry = zip.Entries.First(item => item.FullName.EndsWith("main.lua", StringComparison.Ordinal));
        using var stream = entry.Open();
        stream.SetLength(0);
        stream.Write("return 2"u8);
    }
    Check(SolmodContainer.Verify(container).Count > 0, "a changed file fails verification");

    // Snapshots: take, list, restore, prune.
    var snapshots = new InstallSnapshots(Path.Combine(scratch, "snapshots"));
    var first = snapshots.Take(mods, "my_mod", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    File.WriteAllText(Path.Combine(mod, "scripts", "main.lua"), "return 3");
    snapshots.Take(mods, "my_mod", new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
    Check(first is { Files: 2 } && snapshots.List("my_mod").Count == 2 && snapshots.List("my_mod")[0].Stamp.StartsWith("20260102", StringComparison.Ordinal),
        "snapshots list newest first");
    snapshots.Restore(mods, "my_mod", first!.Stamp);
    Check(File.ReadAllText(Path.Combine(mod, "scripts", "main.lua")) == "return 1" && snapshots.List("my_mod").Count == 3,
        "restore puts the old files back and snapshots the current copy");
    Check(snapshots.Prune("my_mod", 1) == 2 && snapshots.List("my_mod").Count == 1, "prune keeps the newest");
    Check(snapshots.Take(mods, "absent") is null, "no snapshot of a mod that is not installed");
    bool refused = false;
    try { snapshots.Restore(mods, "my_mod", "..\\..\\x"); } catch (DirectoryNotFoundException) { refused = true; }
    Check(refused, "restore refuses stamps outside the snapshot folder");
}
finally
{
    if (scratch.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
}

Check(ContentFamilies.All.Count == 7 && ContentFamilies.All.All(family => family.Notes.Length > 0) && ContentFamilies.NotYetModdable.Count > 0, "family registry is complete");
Console.WriteLine(failures == 0 ? "All content checks passed." : $"{failures} content check(s) failed.");
return failures == 0 ? 0 : 1;
