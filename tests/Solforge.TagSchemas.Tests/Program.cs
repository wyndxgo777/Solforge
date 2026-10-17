// Tag schema registry and game table checks: the embedded schema files load with no problems and keep the offsets the
// hand decoders used; bad schema files are reported, an override folder replaces a schema; validation catches range,
// element class and array bound errors; field-path search; the activity, reward and dialog decoders over fixture bytes
// (synthetic tags built to the schema layouts plus one real 72-byte activity root); and the eight MCP tools over a fake
// package host. No game installation needed.
using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Solforge.Mcp;
using Solforge.Mcp.Data;
using SunriseStudio.Packages.TagSchemas;

var checks = new List<string>();
string scratch = Path.Combine(Path.GetTempPath(), "solforge-tagschema-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    Registry(); checks.Add("registry: embedded schemas load with no problems, every field has a source and status, investment offsets match the old literals");
    BadFiles(); checks.Add("schema files: bad JSON, unknown type, missing provenance, duplicate field, unknown element, field past size are reported");
    Override(); checks.Add("override folder replaces a schema by id and adds new ones");
    Activities(); checks.Add("activity tables: definitions, display names, reward set index; validation clean, then range, class and bound errors caught");
    Rewards(); checks.Add("rewards: activity reward sets and progression reward_items decode with item index and quantity");
    Dialog(); checks.Add("dialog: events, groups, variants and two lines per variant decode; validation clean");
    RealRoot(); checks.Add("real 72-byte activity root 0x80B2EF39 (edz_freeroam): name hash is FNV-1 of the name, two part tags, validates clean");
    Search(); checks.Add("field paths: name[] walks arrays, name-> follows pointers; eq/gt/contains matches");
    await Tools(); checks.Add("MCP tools: names, list/get schema, validate, search, list/get activity, loot, dialog over a fake package host");
    foreach (string check in checks) Console.WriteLine("ok " + check);
    Console.WriteLine($"PASS checks={checks.Count}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error.Message);
    Console.Error.WriteLine(error.StackTrace);
    return 1;
}
finally
{
    if (scratch.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Directory.Exists(scratch)) Directory.Delete(scratch, true);
}

static void Expect(bool condition, string what)
{
    if (!condition) throw new InvalidOperationException(what);
}

void Registry()
{
    var registry = TagSchemaRegistry.Default;
    Expect(registry.Problems.Count == 0, "embedded problems: " + string.Join(" | ", registry.Problems));
    Expect(registry.All.Count >= 30, "schema count " + registry.All.Count);
    foreach (string family in new[] { "investment", "activity", "rewards", "dialog" })
        Expect(registry.All.Any(schema => schema.Family == family), "family " + family);
    Expect(registry.All.All(schema => schema.Provenance.Length > 0 && schema.Fields.All(field => field.Source.Length > 0 && field.Status is "confirmed" or "guessed")), "sources");
    var item = registry.Require(0x80807BEAu);
    Expect(item.Offset("maximum_stack") == 180 && item.Offset("bucket") == 184 && item.Offset("tier") == 186 && item.Offset("instanced") == 187, "item offsets");
    var display = registry.Require(0x80805CE1u);
    Expect(display.Offset("name") == 132 && display.Offset("item_type") == 144 && display.Offset("description") == 152, "display offsets");
    Expect(registry.Require(0x808058EEu).Offset("name") == 64, "vendor name offset");
    var row = registry.Require("investment_index_row");
    Expect(row.Size == 24 && row.Offset("hash") == 0 && row.Offset("tag") == 16, "index row");
    Expect(registry.Require(0x80807850u).Field("sales") is { Stride: 184, ElementClass: 0x80807861 }, "vendor sales");
    Expect(registry.Resolve("0x80808d54")?.Id == "0x80808D54" && registry.Resolve("activity_definition")?.Class == 0x80807711, "resolve");
    // Reflection-derived members come with the record RVA they were read from.
    Expect(item.Fields.Any(field => field.Type == "relptr" && field.Source.StartsWith("reflection 0x80807BEA record 0x3A03200", StringComparison.Ordinal)), "reflection member");
    Expect(registry.Require(0x80808D23u).Guessed.Count() > 5 && registry.Require(0x80808D18u).Guessed.Any() == false, "guessed flags");
}

void BadFiles()
{
    var registry = TagSchemaRegistry.FromSources([
        ("broken.json", "{ not json"),
        ("empty.json", "{}"),
        ("bad.json", """
        { "schemas": [
          { "class": "0x80800001", "name": "unknown type", "provenance": "test", "fields": [ { "name": "a", "offset": 0, "type": "u128", "status": "confirmed", "source": "x" } ] },
          { "class": "0x80800002", "name": "no provenance", "fields": [] },
          { "class": "0x80800003", "name": "duplicate", "provenance": "test", "fields": [
            { "name": "a", "offset": 0, "type": "u32", "status": "confirmed", "source": "x" }, { "name": "A", "offset": 4, "type": "u32", "status": "confirmed", "source": "x" } ] },
          { "class": "0x80800004", "name": "unknown element", "provenance": "test", "size": 24, "fields": [
            { "name": "list", "offset": 8, "type": "array", "status": "confirmed", "source": "x", "element": "nope" } ] },
          { "class": "0x80800005", "name": "past size", "provenance": "test", "size": 8, "fields": [ { "name": "far", "offset": 6, "type": "u32", "status": "guessed", "source": "x" } ] },
          { "class": "0x80800006", "name": "no status", "provenance": "test", "fields": [ { "name": "a", "offset": 0, "type": "u32", "status": "maybe", "source": "x" } ] },
          { "class": "0x80800007", "name": "fine", "provenance": "test", "size": 4, "fields": [ { "name": "a", "offset": "0x0", "type": "u32", "status": "guessed", "source": "x", "allow": ["0xFFFFFFFF"] } ] }
        ] }
        """)]);
    string all = string.Join("\n", registry.Problems);
    foreach (string expected in new[] { "broken.json", "empty.json: no \"schemas\"", "unknown type 'u128'", "needs provenance", "declared twice", "unknown element schema 'nope'", "runs past the struct size", "status must be" })
        Expect(all.Contains(expected, StringComparison.Ordinal), "problem missing: " + expected + "\n" + all);
    Expect(registry.Find(0x80800007) is { } fine && fine.Fields[0].Allow?[0] == 0xFFFFFFFF && registry.Find(0x80800001) is null, "good schema kept, bad dropped");
}

void Override()
{
    string folder = Path.Combine(scratch, "schemas");
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "mine.json"), """
    { "schemas": [
      { "class": "0x80808D18", "name": "Dialog definition (mine)", "family": "dialog", "size": 8, "provenance": "local test",
        "fields": [ { "name": "definition_hash", "offset": 0, "type": "hash", "status": "confirmed", "source": "x" } ] },
      { "id": "my_struct", "name": "Mine", "provenance": "local", "fields": [] }
    ] }
    """);
    var registry = TagSchemaRegistry.LoadEmbedded(folder);
    Expect(registry.Problems.Count == 0, "override problems: " + string.Join(" | ", registry.Problems));
    Expect(registry.Find(0x80808D18)?.Name == "Dialog definition (mine)" && registry.Find(0x80808D18)!.Fields.Count == 1, "override replaced");
    Expect(registry.FindId("my_struct") is not null && registry.All.Count == TagSchemaRegistry.Default.All.Count + 1, "override added");
}

void Activities()
{
    var registry = TagSchemaRegistry.Default;
    byte[] table = Fixtures.ActivityDefinitions();
    var rows = ActivityTables.ReadDefinitions(table);
    Expect(rows.Count == 2, "definition rows " + rows.Count);
    Expect(rows[0] is { Hash: 0x11111111, Name: "mission_test", RequiredLevel: 20, RequiredPower: 750, Type: 6, Destination: 5, UnlockSlot: 1041, MatchmakingConfig: null, HasMovie: false }, "row 0");
    Expect(rows[1] is { Hash: 0x22222222, Name: "patrol_test", MatchmakingConfig: 0x80A00001, HasMovie: true, UnlockSlot: 0xFFFF, GameplaySettings: 0x648999F4 }, "row 1");
    var schema = registry.Require(GameTableTags.ActivityDefinitionClass);
    var issues = TagSchemaReader.Validate(registry, table, schema);
    Expect(issues.Count == 0, "definitions validate: " + string.Join(" | ", issues.Select(issue => issue.Path + " " + issue.Message)));

    byte[] badRange = (byte[])table.Clone();
    badRange[rows[0].Offset + registry.Require("activity_definition").Offset("destination")] = 60;
    Expect(TagSchemaReader.Validate(registry, badRange, schema).Any(issue => issue.Error && issue.Path.EndsWith(".destination", StringComparison.Ordinal) && issue.Message.Contains("above the maximum", StringComparison.Ordinal)), "range error");

    byte[] badClass = (byte[])table.Clone();
    BinaryPrimitives.WriteUInt32LittleEndian(badClass.AsSpan(Fixtures.ArrayHeader(badClass, 8) + 8), 0x80801234);
    Expect(TagSchemaReader.Validate(registry, badClass, schema).Any(issue => issue.Message.Contains("element class 0x80801234", StringComparison.Ordinal)), "class error");

    byte[] badBounds = (byte[])table.Clone();
    BinaryPrimitives.WriteUInt64LittleEndian(badBounds.AsSpan(8), 5000);
    BinaryPrimitives.WriteUInt64LittleEndian(badBounds.AsSpan(Fixtures.ArrayHeader(badBounds, 8)), 5000);
    Expect(TagSchemaReader.Validate(registry, badBounds, schema).Any(issue => issue.Message.Contains("run past the tag end", StringComparison.Ordinal)), "bounds error");

    byte[] badMarker = (byte[])table.Clone();
    BinaryPrimitives.WriteUInt32LittleEndian(badMarker.AsSpan(Fixtures.ArrayHeader(badMarker, 8) - 4), 0);
    Expect(TagSchemaReader.Validate(registry, badMarker, schema).Any(issue => issue.Message.Contains("marker", StringComparison.Ordinal)), "marker error");

    byte[] displays = Fixtures.ActivityDisplays();
    var shown = ActivityTables.ReadDisplays(displays);
    Expect(shown.Count == 2 && shown[0x11111111] is { NameBank: 1, NameHash: 0x1111, DescriptionHash: 0x2222, RewardSet: 0 } && shown[0x22222222].RewardSet == -1, "display rows");
    var displayIssues = TagSchemaReader.Validate(registry, displays, registry.Require(GameTableTags.ActivityDisplayClass));
    Expect(displayIssues.Count == 0, "displays validate: " + string.Join(" | ", displayIssues.Select(issue => issue.Path + " " + issue.Message)));
    Expect(ActivityTables.Fnv1("edz_freeroam") == 0x601D014E && ActivityTables.Fnv1("mission_ember") == 0x1A6AF329, "fnv1");
}

void Rewards()
{
    var sets = RewardTables.ReadActivityRewardSets(Fixtures.ActivityDisplays());
    Expect(sets.Count == 1 && sets[0].Groups.Count == 1 && sets[0].Groups[0].Count == 2, "reward set shape");
    Expect(sets[0].Groups[0][0] is { ItemIndex: 2, Quantity: 0 } && sets[0].Groups[0][1] is { ItemIndex: 1, Quantity: 3 }, "reward items");
    byte[] progressions = Fixtures.Progressions();
    var lists = RewardTables.ReadProgressions(progressions);
    Expect(lists.Count == 1 && lists[0].Hash == 0xABCD0001 && lists[0].Groups[0].Single() is { ItemIndex: 1, Quantity: 5000, Level: 3 }, "progression rewards");
    var issues = TagSchemaReader.Validate(TagSchemaRegistry.Default, progressions, TagSchemaRegistry.Default.Require(GameTableTags.ProgressionClass));
    Expect(issues.Count == 0, "progressions validate: " + string.Join(" | ", issues.Select(issue => issue.Path + " " + issue.Message)));
    var index = RewardTables.ReadIndex(Fixtures.ItemIndex(0x80B00000));
    Expect(index.Length == 3 && index[1] == (200u, 0x80B00001u), "item index rows");
}

void Dialog()
{
    byte[] bytes = Fixtures.DialogTable();
    var table = DialogTables.Read(bytes);
    Expect(table is not null && table.Events.Single() is (0x980735FB, > 13.5f and < 13.7f), "events");
    var group = table!.Groups.Single();
    Expect(group.Definition == 0x980735FB && group.Hash == 0x3981183C && group.Variants.Count == 2, "group");
    var line = group.Variants[0].Lines[0];
    Expect(group.Variants[0].Lines.Count == 2 && line is { Audio: 0x80B4F799, Strings: 0x80B4F79A, StringHash: 0x3620F20C } && Math.Abs(line.Duration - 1.35f) < 0.001f, "lines");
    Expect(group.Variants[1].Lines.Count == 1 && Math.Abs(group.Variants[1].Weight - 0.75f) < 0.001f, "second variant has one line");
    var issues = TagSchemaReader.Validate(TagSchemaRegistry.Default, bytes, TagSchemaRegistry.Default.Require(GameTableTags.DialogTableClass));
    Expect(issues.Count == 0, "dialog validates: " + string.Join(" | ", issues.Select(issue => issue.Path + " " + issue.Message)));
    var view = SolforgeTools.SchemaView(bytes, GameTableTags.DialogTableClass);
    Expect(view?["fields"] is JsonArray fields && fields.Any(field => field?["name"]?.ToString() == "events" && field["value"]?.ToString() == "1 x 0x80808D18 @0x40"), "schema view: " + view);
}

void RealRoot()
{
    byte[] bytes = Convert.FromHexString(Fixtures.EdzFreeroamRoot);
    var root = ActivityTables.ReadRoot(bytes);
    Expect(root is not null && root.NameHash == ActivityTables.Fnv1("edz_freeroam") && root.Parts.SequenceEqual([0x80B2F00Au, 0x80FDB97Fu]), "root decode");
    var issues = TagSchemaReader.Validate(TagSchemaRegistry.Default, bytes, TagSchemaRegistry.Default.Require(GameTableTags.ActivityRootClass));
    Expect(issues.Count == 0, "root validates: " + string.Join(" | ", issues.Select(issue => issue.Path + " " + issue.Message)));
}

void Search()
{
    var registry = TagSchemaRegistry.Default;
    byte[] table = Fixtures.ActivityDefinitions();
    var schema = registry.Require(GameTableTags.ActivityDefinitionClass);
    var names = TagSchemaReader.Values(registry, table, schema, 0, "table[].record->.internal_name").ToList();
    Expect(names.Count == 2 && names[0].Path == "table[0].record->.internal_name" && names[1].Value.Text == "patrol_test", "path walk: " + string.Join(", ", names.Select(hit => hit.Path)));
    Expect(names.Count(hit => TagSchemaReader.Matches(hit.Value, "contains", "PATROL")) == 1, "contains");
    var levels = TagSchemaReader.Values(registry, table, schema, 0, "table[].record->.required_level").ToList();
    Expect(levels.Count(hit => TagSchemaReader.Matches(hit.Value, "gt", "10")) == 1 && levels.Count(hit => TagSchemaReader.Matches(hit.Value, "eq", "0x14")) == 1, "numeric ops");
    Expect(!TagSchemaReader.Values(registry, table, schema, 0, "nope").Any(), "unknown path");
}

async Task Tools()
{
    var host = new FakeData();
    host.Add(GameTableTags.ActivityDefinitions, GameTableTags.ActivityDefinitionClass, Fixtures.ActivityDefinitions(), "activity_definitions");
    host.Add(GameTableTags.ActivityDisplays, GameTableTags.ActivityDisplayClass, Fixtures.ActivityDisplays(), "activity_displays");
    host.Add(GameTableTags.Progressions, GameTableTags.ProgressionClass, Fixtures.Progressions(), "progressions");
    host.Add(GameTableTags.ItemIndex, 0x80807BE4, Fixtures.ItemIndex(0x80B00000), "item_index");
    host.Add(GameTableTags.ItemDisplayIndex, 0x80805CDB, Fixtures.ItemIndex(0x80C00000), "item_display_index");
    for (uint i = 0; i < 3; i++) host.Add(0x80C00000 + i, 0x80805CE1, Fixtures.ItemDisplay(0x3330 + i), $"item_display_{i}");
    host.Add(0x80B4F7A7, GameTableTags.DialogTableClass, Fixtures.DialogTable(), "dialog_test");
    host.Add(0x80B2EF39, GameTableTags.ActivityRootClass, Convert.FromHexString(Fixtures.EdzFreeroamRoot), "edz_freeroam");
    host.Add(0x80F00001, GameTableTags.ActivityRootClass, Fixtures.ActivityRoot("mission_test", 0x80B4F7A7), "mission_test");
    host.Strings[0x1111] = "Test Mission"; host.Strings[0x2222] = "A mission for the checks.";
    host.Strings[0x3331] = "Glimmer"; host.Strings[0x3332] = "Engram"; host.Strings[0x3620F20C] = "Guardian, over here.";

    var tools = SolforgeTools.SchemaTools(host).ToList();
    Expect(tools.Select(tool => tool.Name).SequenceEqual(SolforgeTools.SchemaToolNames) && tools.All(tool => !tool.RequiresApproval), "tool names");
    async Task<JsonNode> Run(string name, JsonObject args, bool error = false)
    {
        var result = await tools.Single(tool => tool.Name == name).Handler(args, CancellationToken.None);
        string text = string.Join("", result.Texts);
        Expect(result.IsError == error, $"{name} error={result.IsError}: {text}");
        return error ? JsonValue.Create(text)! : JsonNode.Parse(text)!;
    }

    var list = await Run("list_tag_schemas", new JsonObject { ["family"] = "dialog", ["limit"] = 500 });
    Expect(list["rows"] is JsonArray schemaRows && schemaRows.Count == 6 && list["problems"] is JsonArray { Count: 0 }, "list_tag_schemas: " + list);
    var one = await Run("get_tag_schema", new JsonObject { ["schema"] = "0x80808D23" });
    Expect(one["fields"] is JsonArray variantFields && variantFields.Any(field => field?["name"]?.ToString() == "line1_string" && field["status"]?.ToString() == "confirmed"), "get_tag_schema");
    await Run("get_tag_schema", new JsonObject { ["schema"] = "0x80801111" }, error: true);

    var valid = await Run("validate_tag", new JsonObject { ["hash"] = "0x80B4F7A7" });
    Expect(valid["checked"]?.GetValue<int>() == 1 && valid["failed"]?.GetValue<int>() == 0, "validate dialog: " + valid);
    var many = await Run("validate_tag", new JsonObject { ["class"] = "0x80808AAE" });
    Expect(many["checked"]?.GetValue<int>() == 2 && many["failed"]?.GetValue<int>() == 0, "validate roots: " + many);
    byte[] broken = Fixtures.ActivityDefinitions();
    broken[ActivityTables.ReadDefinitions(broken)[1].Offset + TagSchemaRegistry.Default.Require("activity_definition").Offset("activity_type")] = 200;
    host.Add(0x80F00002, GameTableTags.ActivityDefinitionClass, broken, "broken");
    var bad = await Run("validate_tag", new JsonObject { ["hash"] = "0x80F00002" });
    Expect(bad["failed"]?.GetValue<int>() == 1 && bad.ToJsonString().Contains("activity_type", StringComparison.Ordinal), "validate broken: " + bad);

    var found = await Run("search_tags_by_field", new JsonObject { ["class"] = "0x808076F0", ["field"] = "table[].record->.internal_name", ["value"] = "mission_test" });
    Expect(found["rows"] is JsonArray hits && hits.Count == 2 && hits.All(hit => hit?["path"]?.ToString() == "table[0].record->.internal_name"), "search: " + found);
    await Run("search_tags_by_field", new JsonObject { ["class"] = "0x808076F0", ["field"] = "missing", ["value"] = "1" }, error: true);

    var activities = await Run("list_activities", new JsonObject { ["filter"] = "test mission" });
    Expect(activities["rows"] is JsonArray activityRows && activityRows.Count == 1 && activityRows[0]?["internal_name"]?.ToString() == "mission_test", "list_activities: " + activities);
    var activity = await Run("get_activity", new JsonObject { ["activity"] = "mission_test" });
    Expect(activity["name"]?.ToString() == "Test Mission" && activity["description"]?.ToString() == "A mission for the checks.", "get_activity text: " + activity);
    Expect(activity["rewards"]?["groups"]?[0]?[1]?["name"]?.ToString() == "Glimmer" && activity["root"]?["tag"]?["hash"]?.ToString() == "0x80F00001", "get_activity rewards/root: " + activity);
    Expect(activity["fields"] is JsonArray activityFields && activityFields.Any(field => field?["name"]?.ToString() == "level_2" && field["status"]?.ToString() == "guessed"), "get_activity fields");

    var loot = await Run("get_loot_table", new JsonObject { ["source"] = "progression", ["index"] = 0 });
    Expect(loot["groups"]?[0]?[0]?["quantity"]?.GetValue<long>() == 5000 && loot["groups"]?[0]?[0]?["item_hash"]?.GetValue<uint>() == 200 && loot["groups"]?[0]?[0]?["name"]?.ToString() == "Glimmer", "progression loot: " + loot);
    var sets = await Run("get_loot_table", new JsonObject());
    Expect(sets["rows"] is JsonArray setRows && setRows.Count == 1 && setRows[0]?["items"]?.GetValue<int>() == 2, "loot listing: " + sets);
    var byActivity = await Run("get_loot_table", new JsonObject { ["activity"] = "patrol_test" });
    Expect(byActivity["rewards"] is null && byActivity["note"] is not null, "activity without rewards: " + byActivity);

    var dialogs = await Run("get_dialog_table", new JsonObject());
    Expect(dialogs["rows"] is JsonArray dialogRows && dialogRows.Count == 1, "dialog listing");
    var dialog = await Run("get_dialog_table", new JsonObject { ["hash"] = "0x80B4F7A7" });
    Expect(dialog["groups"]?[0]?["variants"]?[0]?["lines"]?[0]?["text"]?.ToString() == "Guardian, over here.", "dialog text: " + dialog);
    await Run("get_dialog_table", new JsonObject { ["hash"] = "0x80B2EF39" }, error: true);
    var none = await SolforgeTools.SchemaTools((ISolforgeDataHost?)null).Single(tool => tool.Name == "list_activities").Handler(new JsonObject(), CancellationToken.None);
    Expect(none.IsError, "no data host");
}

/** Builds tag bytes the way the packages lay them out: root struct, then typed arrays (marker, count, class, data) and pointed-to blocks. */
sealed class TagBuilder(int rootSize)
{
    private byte[] data = new byte[rootSize];
    public int Length { get; private set; } = rootSize;

    private void Ensure(int size)
    {
        if (size > data.Length) System.Array.Resize(ref data, Math.Max(size, data.Length * 2));
        Length = Math.Max(Length, size);
    }

    /** A zeroed block at the end, aligned; returns its offset. A class hash, when given, goes in the 4 bytes before it. */
    public int Block(int size, uint classBefore = 0)
    {
        int at = (Length + (classBefore != 0 ? 4 : 0) + 7) & ~7;
        Ensure(at + size);
        if (classBefore != 0) U32(at - 4, classBefore);
        return at;
    }

    /** A typed array for the descriptor at field: marker, header (count, element class) and count * stride zeroed bytes; returns the data offset. */
    public int Array(int field, uint elementClass, int count, int stride)
    {
        int header = Block(16 + count * stride, 0x80809FBD);
        U64(header, (ulong)count); U32(header + 8, elementClass);
        U64(field, (ulong)count); I64(field + 8, header - (field + 8));
        return header + 16;
    }

    public void Pointer(int field, int target) => I64(field, target - field);
    public int CString(string text)
    {
        int at = Block(text.Length + 1);
        for (int i = 0; i < text.Length; i++) data[at + i] = (byte)text[i];
        return at;
    }
    public void U8(int at, byte value) { Ensure(at + 1); data[at] = value; }
    public void U16(int at, ushort value) { Ensure(at + 2); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), value); }
    public void U32(int at, uint value) { Ensure(at + 4); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value); }
    public void I32(int at, int value) { Ensure(at + 4); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(at), value); }
    public void F32(int at, float value) { Ensure(at + 4); BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at), value); }
    public void U64(int at, ulong value) { Ensure(at + 8); BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at), value); }
    public void I64(int at, long value) { Ensure(at + 8); BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(at), value); }

    /** The finished tag: the u64 size at 0, then the bytes. */
    public byte[] Build()
    {
        byte[] result = data[..Length];
        BinaryPrimitives.WriteUInt64LittleEndian(result, (ulong)result.Length);
        return result;
    }
}

/** Fixture tags. Offsets come from the schema files so a fixture always matches the layout being checked. */
static class Fixtures
{
    private static TagSchemaRegistry R => TagSchemaRegistry.Default;

    /** The shipped 72-byte activity root 0x80B2EF39 (edz_freeroam, w64_edz_activities): name hash 0x601D014E and two part tags. */
    public const string EdzFreeroamRoot = "48000000000000004E011D600000000002000000000000001800000000000000000000000000000000000000BD9F8080" +
        "020000000000000091848080000000000AF0B2807FB9FD80";

    /** Offset of the array header (after the marker) of the descriptor at field. */
    public static int ArrayHeader(byte[] bytes, int field) => field + 8 + (int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field + 8));

    public static byte[] ActivityDefinitions()
    {
        var record = R.Require("activity_definition");
        var tag = new TagBuilder(136);
        int rows = tag.Array(8, 0x808076FC, 2, 16);
        (uint Hash, string Name, int Level, int Power, ushort Slot, byte Type, uint Settings, byte Destination, uint Config, bool Movie)[] values =
        [
            (0x11111111, "mission_test", 20, 750, 1041, 6, 0x4D78882E, 5, uint.MaxValue, false),
            (0x22222222, "patrol_test", 5, 0, ushort.MaxValue, 10, 0x648999F4, 2, 0x80A00001, true)
        ];
        for (int i = 0; i < values.Length; i++)
        {
            var value = values[i];
            int row = rows + i * 16;
            tag.U32(row, value.Hash);
            int start = tag.Block(0x160, 0x80807711);
            tag.Pointer(row + R.Require(0x808076FCu).Offset("record"), start);
            tag.U32(start + record.Offset("hash"), value.Hash);
            int config = tag.Block(4); tag.U32(config, value.Config); tag.Pointer(start + record.Offset("matchmaking_config"), config);
            if (value.Movie) { int movie = tag.Block(8); tag.Pointer(start + record.Offset("movie_route"), movie); }
            tag.Pointer(start + record.Offset("internal_name"), tag.CString(value.Name));
            tag.I32(start + record.Offset("required_level"), value.Level); tag.I32(start + record.Offset("required_power"), value.Power);
            tag.I32(start + record.Offset("level_2"), value.Level); tag.I32(start + record.Offset("power_2"), value.Power);
            tag.U16(start + record.Offset("unlock_slot"), value.Slot); tag.U8(start + record.Offset("activity_type"), value.Type);
            tag.U32(start + record.Offset("gameplay_settings"), value.Settings); tag.U8(start + record.Offset("destination"), value.Destination);
        }
        return tag.Build();
    }

    public static byte[] ActivityDisplays()
    {
        var table = R.Require(0x80805E0Au);
        var record = R.Require(0x80805E18u);
        var display = R.Require(0x80805E23u);
        var tag = new TagBuilder(table.Size);
        int index = tag.Array(table.Offset("table"), 0x80805E15, 2, 16);
        int texts = tag.Array(table.Offset("display_rows"), 0x80805E23, 2, 36);
        int sets = tag.Array(table.Offset("reward_sets"), 0x80805E3E, 1, 16);
        int groups = tag.Array(sets + R.Require(0x80805E3Eu).Offset("groups"), 0x80805E40, 1, 24);
        tag.U16(groups, 0xFFFF); tag.U32(groups + 4, 0x811C9DC5);
        int items = tag.Array(groups + R.Require(0x80805E40u).Offset("reward_items"), 0x80805E42, 2, 8);
        tag.U16(items, 2); tag.U16(items + 8, 1); tag.U32(items + 12, 3);
        uint[] hashes = [0x11111111, 0x22222222];
        for (int i = 0; i < 2; i++)
        {
            int row = index + i * 16, text = texts + i * 36;
            tag.U32(row, hashes[i]);
            int start = tag.Block(record.Size, 0x80805E18);
            tag.Pointer(row + R.Require(0x80805E15u).Offset("record"), start);
            tag.Pointer(start + record.Offset("display_row"), text);
            tag.U16(start + record.Offset("reward_set_index"), i == 0 ? (ushort)0 : ushort.MaxValue);
            tag.U32(text, 0xFFFF);
            tag.U32(text + display.Offset("name"), 1); tag.U32(text + display.Offset("name") + 4, i == 0 ? 0x1111u : 0x5555u);
            tag.U32(text + display.Offset("description"), 1); tag.U32(text + display.Offset("description") + 4, i == 0 ? 0x2222u : 0x6666u);
        }
        return tag.Build();
    }

    public static byte[] Progressions()
    {
        var progression = R.Require(0x80807CDDu);
        var reward = R.Require(0x80802FF7u);
        var tag = new TagBuilder(24);
        int rows = tag.Array(8, 0x80807CDD, 1, progression.Size);
        tag.U32(rows + progression.Offset("hash"), 0xABCD0001);
        int rewards = tag.Array(rows + progression.Offset("reward_items"), 0x80802FF7, 1, reward.Size);
        tag.I32(rewards + reward.Offset("rewarded_at_level"), 3); tag.U32(rewards + reward.Offset("item_index"), 1);
        tag.I32(rewards + reward.Offset("quantity"), 5000); tag.U16(rewards + reward.Offset("unknown_10"), 1); tag.U32(rewards + reward.Offset("sequence"), 7);
        return tag.Build();
    }

    /** An investment index with item hashes 100, 200, 300 and tags firstTag + row. */
    public static byte[] ItemIndex(uint firstTag)
    {
        var row = R.Require("investment_index_row");
        var tag = new TagBuilder(24);
        int rows = tag.Array(8, 0x80807BE8, 3, row.Size);
        for (int i = 0; i < 3; i++) { tag.U32(rows + i * row.Size + row.Offset("hash"), (uint)(i + 1) * 100); tag.U32(rows + i * row.Size + row.Offset("tag"), firstTag + (uint)i); }
        return tag.Build();
    }

    public static byte[] ItemDisplay(uint nameHash)
    {
        var display = R.Require(0x80805CE1u);
        var tag = new TagBuilder(display.Size);
        tag.U32(display.Offset("name") + 4, nameHash);
        return tag.Build();
    }

    public static byte[] ActivityRoot(string name, uint part)
    {
        var root = R.Require(GameTableTags.ActivityRootClass);
        var tag = new TagBuilder(root.Size);
        tag.U32(root.Offset("name_hash"), ActivityTables.Fnv1(name));
        int parts = tag.Array(root.Offset("parts"), 0x80808491, 1, 4);
        tag.U32(parts, part);
        return tag.Build();
    }

    /** Mirrors the shipped 512-byte dialog table 0x80B4F7A7: one event, one group, two variants (the second with one line). */
    public static byte[] DialogTable()
    {
        var group = R.Require(0x80808D19u);
        var content = R.Require(0x80808D1Du);
        var variant = R.Require(0x80808D23u);
        var tag = new TagBuilder(40);
        int events = tag.Array(8, 0x80808D18, 1, 8);
        tag.U32(events, 0x980735FB); tag.F32(events + 4, 13.633f);
        int groups = tag.Array(24, 0x80808D19, 1, 16);
        tag.U32(groups, 0x980735FB);
        int body = tag.Block(content.Size, 0x80808D1D);
        tag.Pointer(groups + group.Offset("content"), body);
        tag.U32(body, 0x3981183C); tag.U32(body + 4, 0x980735FB); tag.U32(body + 8, 0xC); tag.U32(body + 12, 0x166521A5);
        int pointers = tag.Array(body + content.Offset("variants"), 0x80808D20, 2, 8);
        for (int v = 0; v < 2; v++)
        {
            int start = tag.Block(variant.Size, 0x80808D23);
            tag.Pointer(pointers + v * 8, start);
            tag.U32(start, 0xA599A5A8 + (uint)v); tag.U32(start + 4, 0x980735FB); tag.U32(start + 8, 0xE); tag.U32(start + 12, 0x6D96BFB0);
            tag.F32(start + variant.Offset("delay"), 0.6f); tag.F32(start + variant.Offset("weight"), v == 0 ? 1f : 0.75f);
            tag.U32(start + variant.Offset("line1_audio"), 0x80B4F799); tag.U32(start + variant.Offset("line1_strings"), 0x80B4F79A);
            tag.U32(start + variant.Offset("line1_string"), 0x3620F20C); tag.F32(start + variant.Offset("line1_duration"), 1.35f);
            if (v == 0)
            {
                tag.U32(start + variant.Offset("line2_audio"), 0x80B4F79B); tag.U32(start + variant.Offset("line2_strings"), 0x80B4F79C);
                tag.U32(start + variant.Offset("line2_string"), 0xDE954ED9); tag.F32(start + variant.Offset("line2_duration"), 2.5f);
            }
            tag.U32(start + variant.Offset("tail_hash"), 0x811C9DC5); tag.U32(start + variant.Offset("tail_hash_2"), 0x79EF9437); tag.F32(start + variant.Offset("tail_value"), 10f);
        }
        return tag.Build();
    }
}

/** Package data host over in-memory tags and strings. */
sealed class FakeData : ISolforgeDataHost
{
    private readonly Dictionary<uint, (TagSummary Summary, byte[] Bytes)> tags = new();
    public Dictionary<uint, string> Strings { get; } = new();
    public void Add(uint hash, uint type, byte[] bytes, string name) => tags[hash] = (new TagSummary(hash, name, type, $"class_{type:X8}", "w64_test", bytes.Length), bytes);
    public string? DataUnavailable => null;
    public TagSummary? Tag(uint hash) => tags.TryGetValue(hash, out var tag) ? tag.Summary : null;
    public bool TagExists(uint hash) => tags.ContainsKey(hash);
    public byte[]? ReadTag(uint hash, bool original) => tags.TryGetValue(hash, out var tag) ? (byte[])tag.Bytes.Clone() : null;
    public IEnumerable<TagSummary> TagsOfClass(uint classHash) => tags.Values.Select(tag => tag.Summary).Where(tag => tag.Class == classHash).OrderBy(tag => tag.Hash);
    public IReadOnlyDictionary<uint, string> ClassLabels { get; } = new Dictionary<uint, string>();
    public IReadOnlyDictionary<uint, int> ClassCounts() => tags.Values.GroupBy(tag => tag.Summary.Class).ToDictionary(group => group.Key, group => group.Count());
    public Task<IReadOnlyList<string>> StringsAsync(uint hash, CancellationToken cancel) =>
        Task.FromResult<IReadOnlyList<string>>(Strings.TryGetValue(hash, out string? text) ? [text] : []);
    public Task<IReadOnlyList<(uint Hash, string Text, uint? Container)>> SearchStringsAsync(string query, int limit, CancellationToken cancel) =>
        Task.FromResult<IReadOnlyList<(uint, string, uint?)>>([]);
    public string? StringIfLoaded(uint hash) => Strings.GetValueOrDefault(hash);
}
