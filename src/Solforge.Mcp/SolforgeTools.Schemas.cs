/**
 * File: MCP tools over the tag schema registry (list_tag_schemas, get_tag_schema, validate_tag, search_tags_by_field) and
 * the read-only game table decoders built on it (list_activities, get_activity, get_loot_table, get_dialog_table).
 */
using System.Globalization;
using System.Text.Json.Nodes;
using Solforge.Mcp.Data;
using SunriseStudio.Packages.TagSchemas;

namespace Solforge.Mcp;

/**
 * Schema tools: the registry (src/SunriseStudio.Packages/TagSchemas/schemas/*.json) names the fields of known tag classes,
 * each with its provenance (reflection record, Sunrise client reader, hand decoder, data) and a confirmed or guessed
 * status. dump_tag and get_tag add the same named fields; the game table tools decode activities, reward lists and
 * dialog with offsets taken from the registry. Everything here reads; nothing writes.
 */
public static partial class SolforgeTools
{
    public static readonly string[] SchemaToolNames =
    [
        "list_tag_schemas", "get_tag_schema", "validate_tag", "search_tags_by_field", "list_activities", "get_activity", "get_loot_table", "get_dialog_table"
    ];

    /** Named root fields of a tag whose class has a schema, with a validation summary; null when the class has none. */
    public static JsonObject? SchemaView(byte[] bytes, uint classHash, Func<uint, string?>? text = null, TagSchemaRegistry? schemas = null)
    {
        schemas ??= TagSchemaRegistry.Default;
        if (schemas.Find(classHash) is not { } schema) return null;
        var fields = new JsonArray();
        foreach (var value in TagSchemaReader.ReadAll(bytes, 0, schema)) fields.Add(FieldRow(value, text));
        var issues = TagSchemaReader.Validate(schemas, bytes, schema, 0, depth: 2, elementLimit: 64);
        return new JsonObject
        {
            ["schema"] = schema.Id, ["name"] = schema.Name, ["family"] = schema.Family, ["provenance"] = schema.Provenance, ["fields"] = fields,
            ["errors"] = issues.Count(issue => issue.Error), ["warnings"] = issues.Count(issue => !issue.Error),
            ["issues"] = new JsonArray([.. issues.Take(8).Select(IssueRow)])
        };
    }

    /** get_tag's result with the registry's named fields added under "named" when the host can read the tag and knows its class. */
    public static JsonObject WithSchemaFields(ISolforgeHost host, uint hash, JsonObject tag)
    {
        if (host is ISolforgeDataHost { DataUnavailable: null } data && data.Tag(hash) is { } summary && data.ReadTag(hash, false) is { } bytes
            && SchemaView(bytes, summary.Class, data.StringIfLoaded) is { } named)
            tag["named"] = named;
        return tag;
    }

    private static JsonObject FieldRow(TagFieldValue value, Func<uint, string?>? text)
    {
        var row = new JsonObject
        {
            ["name"] = value.Field.Name, ["offset"] = Hex(value.Offset), ["type"] = value.Field.Type, ["value"] = value.Text, ["status"] = value.Field.Status
        };
        if (text is not null && value.Field.Type is "string_ref" or "string_ref16" or "string_hash" && value.Raw is { } hash && text((uint)hash) is { } words)
            row["text"] = words.Length > 120 ? words[..120] + "..." : words;
        if (value.Field.Note is { } note) row["note"] = note;
        return row;
    }

    private static JsonObject IssueRow(TagSchemaIssue issue) => new()
    {
        ["path"] = issue.Path, ["offset"] = Hex(issue.Offset), ["message"] = issue.Message, ["severity"] = issue.Error ? "error" : "warning"
    };

    private static JsonObject SchemaRow(TagSchema schema) => new()
    {
        ["id"] = schema.Id, ["class"] = schema.Class is { } type ? Hex(type) : null, ["name"] = schema.Name, ["family"] = schema.Family,
        ["size"] = schema.Size, ["fields"] = schema.Fields.Count, ["guessed"] = schema.Guessed.Count(), ["decoders"] = new JsonArray([.. schema.Decoders.Select(name => (JsonNode)name)]),
        ["provenance"] = schema.Provenance, ["file"] = schema.File
    };

    private static JsonObject SchemaFieldRow(TagSchemaField field)
    {
        var row = new JsonObject
        {
            ["name"] = field.Name, ["offset"] = Hex(field.Offset), ["type"] = field.Type, ["status"] = field.Status, ["source"] = field.Source
        };
        if (field.Note is { } note) row["note"] = note;
        if (field.ElementClass is { } element) row["element_class"] = Hex(element);
        if (field.Element is { } id) row["element"] = id;
        if (field.Stride > 0) row["stride"] = field.Stride;
        if (field.MaxCount > 0) row["max_count"] = field.MaxCount;
        if (field.Min is { } min) row["min"] = min;
        if (field.Max is { } max) row["max"] = max;
        if (field.Allow is { Count: > 0 } allow) row["allow"] = new JsonArray([.. allow.Select(value => (JsonNode)("0x" + value.ToString("X", CultureInfo.InvariantCulture)))]);
        if (field.Target is { } target) row["target"] = target;
        return row;
    }

    /** Yields the schema and game table tools. */
    private static IEnumerable<McpTool> SchemaTools(ISolforgeHost host) => SchemaTools(host as ISolforgeDataHost);

    /** The schema and game table tools over package data alone (null data: the registry tools still work, the rest report it). */
    public static IEnumerable<McpTool> SchemaTools(ISolforgeDataHost? packageData)
    {
        static JsonObject Paged(params (string, string, string, bool)[] properties) => McpJson.Schema([.. properties, .. DataPages.Args]);
        static string A(JsonObject args, string name) => McpJson.String(args, name) ?? "";
        var schemas = TagSchemaRegistry.Default;

        McpTool Pkg(string name, string description, JsonObject schema, Func<ISolforgeDataHost, JsonObject, CancellationToken, Task<McpToolResult>> run) =>
            new(name, description, schema, false, async (args, cancel) =>
            {
                if (packageData is not { } data) return McpToolResult.Error("This Solforge host has no package data access.");
                if (data.DataUnavailable is { } reason) return McpToolResult.Error(reason);
                return await run(data, args, cancel).ConfigureAwait(false);
            });

        async Task<string?> Text(ISolforgeDataHost data, uint hash, CancellationToken cancel)
        {
            if (hash is 0 or 0x811C9DC5 or uint.MaxValue) return null;
            if (data.StringIfLoaded(hash) is { } loaded) return loaded;
            var texts = await data.StringsAsync(hash, cancel).ConfigureAwait(false);
            return texts.Count > 0 ? texts[0] : null;
        }

        // ---- registry ----

        yield return new McpTool("list_tag_schemas", "Tag classes the schema registry names fields for (activity, rewards, dialog, investment...): " +
            "id, class, struct size, field count, how many fields are guessed, which decoders use it and where the layout comes from. " +
            "'problems' lists schema files that failed to load.",
            Paged(("family", "string", "Only this family (activity, rewards, dialog, investment)", false), ("filter", "string", "Text in the id, class or name", false)),
            false, (args, _) =>
            {
                string family = A(args, "family"), filter = A(args, "filter");
                var rows = schemas.All.Where(schema => (family.Length == 0 || schema.Family.Equals(family, StringComparison.OrdinalIgnoreCase))
                    && (filter.Length == 0 || schema.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) || schema.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)));
                var page = DataPages.Page(rows, args, SchemaRow);
                page["problems"] = new JsonArray([.. schemas.Problems.Select(problem => (JsonNode)problem)]);
                return Task.FromResult(McpToolResult.Json(page));
            });

        yield return new McpTool("get_tag_schema", "One schema with every field: offset, type, confirmed or guessed, its source (reflection record RVA, " +
            "Sunrise client reader, Solforge decoder or data sample), ranges, array element class and stride, pointer targets.",
            McpJson.Schema(("schema", "string", "Schema id or class hash (0x80808D54)", true)), false, (args, _) =>
            {
                if (schemas.Resolve(A(args, "schema")) is not { } schema)
                    return Task.FromResult(McpToolResult.Error("No schema with that id or class. list_tag_schemas lists them; infer_tag_schema guesses one for an unknown class."));
                var result = SchemaRow(schema);
                result["fields"] = new JsonArray([.. schema.Fields.Select(SchemaFieldRow)]);
                return Task.FromResult(McpToolResult.Json(result));
            });

        yield return Pkg("validate_tag", "Check tag bytes against the registry schema of their class: field types (tag references, floats, booleans), " +
            "ranges, typed arrays (marker, count, element class, stride inside the tag, max count), recursing into elements and pointer " +
            "targets. Give a tag hash, or a class to check up to 'samples' of its tags.",
            McpJson.Schema(("hash", "string", "Tag hash", false), ("class", "string", "Class hash: check many tags of it", false),
                ("samples", "integer", "Tags to check for a class (default 50, max 1000)", false), ("original", "boolean", "Shipped bytes, ignoring project edits", false)),
            (data, args, cancel) =>
            {
                bool original = McpJson.Bool(args, "original", false);
                IEnumerable<TagSummary> tags;
                if (ParseHash(McpJson.String(args, "hash")) is { } hash) tags = data.Tag(hash) is { } one ? new[] { one } : Array.Empty<TagSummary>();
                else if (ParseHash(McpJson.String(args, "class")) is { } type) tags = data.TagsOfClass(type).Take(Math.Clamp(McpJson.Int(args, "samples", 50), 1, 1000));
                else return Task.FromResult(McpToolResult.Error("Give a tag hash or a class hash."));
                var rows = new JsonArray();
                int checkedTags = 0, failed = 0;
                foreach (var tag in tags)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (schemas.Find(tag.Class) is not { } schema) return Task.FromResult(McpToolResult.Error($"No schema for class {Hex(tag.Class)}. list_tag_schemas lists the known ones."));
                    if (data.ReadTag(tag.Hash, original) is not { } bytes) continue;
                    var issues = TagSchemaReader.Validate(schemas, bytes, schema);
                    checkedTags++;
                    if (issues.Any(issue => issue.Error)) failed++;
                    if (issues.Count > 0 || rows.Count < 20)
                        rows.Add(new JsonObject
                        {
                            ["tag"] = Hex(tag.Hash), ["name"] = tag.Name, ["errors"] = issues.Count(issue => issue.Error), ["warnings"] = issues.Count(issue => !issue.Error),
                            ["issues"] = new JsonArray([.. issues.Take(20).Select(IssueRow)])
                        });
                }
                if (checkedTags == 0) return Task.FromResult(McpToolResult.Error("No readable tags."));
                return Task.FromResult(McpToolResult.Json(new JsonObject { ["checked"] = checkedTags, ["failed"] = failed, ["tags"] = rows }));
            });

        yield return Pkg("search_tags_by_field", "Find tags of a class whose schema field matches a value. 'field' is a path of schema field names: " +
            "'destination', 'table[].record->.internal_name' (name[] walks every array element, name-> follows a pointer). op is eq (default), " +
            "ne, lt, le, gt, ge or contains; numbers may be decimal or 0x hex. get_tag_schema shows the field names.",
            Paged(("class", "string", "Class hash with a schema", true), ("field", "string", "Field path", true), ("value", "string", "Value to compare", true),
                ("op", "string", "eq, ne, lt, le, gt, ge or contains", false), ("samples", "integer", "Tags to scan (default 2000, max 20000)", false)),
            (data, args, cancel) =>
            {
                if (ParseHash(McpJson.String(args, "class")) is not { } type || schemas.Find(type) is not { } schema)
                    return Task.FromResult(McpToolResult.Error("Give a class hash that has a schema (list_tag_schemas)."));
                string path = A(args, "field"), value = A(args, "value"), op = (McpJson.String(args, "op") ?? "eq").ToLowerInvariant();
                if (path.Length == 0) return Task.FromResult(McpToolResult.Error("Give a field path."));
                string first = path.Split('.')[0].TrimEnd('[', ']', '-', '>');
                if (schema.Field(first) is null) return Task.FromResult(McpToolResult.Error($"{schema.Id} has no field '{first}'. Fields: {string.Join(", ", schema.Fields.Select(field => field.Name))}."));
                int limit = Math.Clamp(McpJson.Int(args, "samples", 2000), 1, 20000);
                IEnumerable<(TagSummary Tag, string Path, TagFieldValue Value)> Hits()
                {
                    foreach (var tag in data.TagsOfClass(type).Take(limit))
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (data.ReadTag(tag.Hash, false) is not { } bytes) continue;
                        foreach (var (where, found) in TagSchemaReader.Values(schemas, bytes, schema, 0, path))
                            if (TagSchemaReader.Matches(found, op, value)) yield return (tag, where, found);
                    }
                }
                return Task.FromResult(McpToolResult.Json(DataPages.Page(Hits(), args, hit => new JsonObject
                {
                    ["tag"] = Hex(hit.Tag.Hash), ["name"] = hit.Tag.Name, ["path"] = hit.Path, ["offset"] = Hex(hit.Value.Offset), ["value"] = hit.Value.Text
                }, countAll: false)));
            });

        // ---- game tables ----

        yield return Pkg("list_activities", "Activities from the activity definition table (0x81327CF0) with their localized names from the display " +
            "table (0x81327D35): hash, internal name, type, destination index, required level and power, unlock slot, reward set. Read-only.",
            Paged(("filter", "string", "Text in the internal or localized name, or the hash", false)),
            async (data, args, cancel) =>
            {
                if (data.ReadTag(GameTableTags.ActivityDefinitions, false) is not { } definitions) return McpToolResult.Error("The activity definition table is not in this installation.");
                var displays = data.ReadTag(GameTableTags.ActivityDisplays, false) is { } display ? ActivityTables.ReadDisplays(display) : new Dictionary<uint, ActivityDisplay>();
                string filter = A(args, "filter");
                var rows = new List<JsonObject>();
                foreach (var activity in ActivityTables.ReadDefinitions(definitions))
                {
                    var shown = displays.GetValueOrDefault(activity.Hash);
                    string? name = shown is null ? null : await Text(data, shown.NameHash, cancel).ConfigureAwait(false);
                    if (filter.Length > 0 && !activity.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) && name?.Contains(filter, StringComparison.OrdinalIgnoreCase) != true
                        && !Hex(activity.Hash).Contains(filter, StringComparison.OrdinalIgnoreCase) && activity.Hash.ToString(CultureInfo.InvariantCulture) != filter) continue;
                    rows.Add(ActivityRow(activity, name, shown));
                }
                return McpToolResult.Json(DataPages.Page(rows, args, row => row));
            });

        yield return Pkg("get_activity", "One activity by hash or internal name: every definition field named by the registry (with confirmed/guessed), " +
            "localized name and description, its reward set with item hashes and names, and the per-activity root tag (class 0x80808AAE) " +
            "whose name hash matches, with the tags it pulls in.",
            McpJson.Schema(("activity", "string", "Activity hash (0x... or decimal) or internal name such as mission_ember", true)),
            async (data, args, cancel) =>
            {
                if (data.ReadTag(GameTableTags.ActivityDefinitions, false) is not { } definitions) return McpToolResult.Error("The activity definition table is not in this installation.");
                string wanted = A(args, "activity").Trim();
                var all = ActivityTables.ReadDefinitions(definitions);
                uint? hash = ParseHash(wanted);
                var activity = all.FirstOrDefault(row => row.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? (hash is { } value ? all.FirstOrDefault(row => row.Hash == value) : null)
                    ?? (uint.TryParse(wanted, NumberStyles.None, CultureInfo.InvariantCulture, out uint number) ? all.FirstOrDefault(row => row.Hash == number) : null);
                if (activity is null) return McpToolResult.Error("No such activity. list_activities lists them.");
                byte[]? displayBytes = data.ReadTag(GameTableTags.ActivityDisplays, false);
                var shown = displayBytes is null ? null : ActivityTables.ReadDisplays(displayBytes).GetValueOrDefault(activity.Hash);
                string? name = shown is null ? null : await Text(data, shown.NameHash, cancel).ConfigureAwait(false);
                var result = ActivityRow(activity, name, shown);
                if (shown is not null) result["description"] = await Text(data, shown.DescriptionHash, cancel).ConfigureAwait(false);
                var record = schemas.Require("activity_definition");
                result["fields"] = new JsonArray([.. TagSchemaReader.ReadAll(definitions, activity.Offset, record).Select(value => (JsonNode)FieldRow(value, data.StringIfLoaded))]);
                if (shown is { RewardSet: >= 0 } && displayBytes is not null)
                {
                    var sets = RewardTables.ReadActivityRewardSets(displayBytes);
                    if (shown.RewardSet < sets.Count) result["rewards"] = await RewardJson(data, sets[shown.RewardSet], cancel).ConfigureAwait(false);
                }
                if (activity.Name.Length > 0)
                {
                    uint nameHash = ActivityTables.Fnv1(activity.Name);
                    foreach (var tag in data.TagsOfClass(GameTableTags.ActivityRootClass))
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (data.ReadTag(tag.Hash, false) is not { } bytes || ActivityTables.ReadRoot(bytes) is not { } root || root.NameHash != nameHash) continue;
                        result["root"] = new JsonObject
                        {
                            ["tag"] = TagRow(tag), ["name_hash"] = Hex(root.NameHash),
                            ["parts"] = new JsonArray([.. root.Parts.Select(part => (JsonNode)(data.Tag(part) is { } summary ? TagRow(summary) : new JsonObject { ["hash"] = Hex(part) }))])
                        };
                        break;
                    }
                }
                result["schema_note"] = "Fields marked guessed are data-only; see get_tag_schema activity_definition for each field's source.";
                return McpToolResult.Json(result);
            });

        yield return Pkg("get_loot_table", "Reward lists the client ships, with item hashes and names: source=activity (the display table's reward " +
            "sets; give index or activity) or source=progression (0x8131933C reward_items per progression; give index or hash). Without index, " +
            "activity or hash it lists the sets with their item counts. Drop rolls (real loot tables) are server-side and not in the client.",
            Paged(("source", "string", "activity (default) or progression", false), ("index", "integer", "Reward set or progression index", false),
                ("activity", "string", "Activity hash or internal name (source=activity)", false), ("hash", "string", "Progression hash (source=progression)", false)),
            async (data, args, cancel) =>
            {
                bool progression = A(args, "source").Equals("progression", StringComparison.OrdinalIgnoreCase);
                uint tagHash = progression ? GameTableTags.Progressions : GameTableTags.ActivityDisplays;
                if (data.ReadTag(tagHash, false) is not { } bytes) return McpToolResult.Error($"Tag {Hex(tagHash)} is not in this installation.");
                var lists = progression ? RewardTables.ReadProgressions(bytes) : RewardTables.ReadActivityRewardSets(bytes);
                int index = McpJson.Int(args, "index", -1);
                if (!progression && A(args, "activity") is { Length: > 0 } wanted && data.ReadTag(GameTableTags.ActivityDefinitions, false) is { } definitions)
                {
                    uint? parsed = ParseHash(wanted);
                    var activity = ActivityTables.ReadDefinitions(definitions).FirstOrDefault(row => row.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) || row.Hash == parsed);
                    if (activity is null) return McpToolResult.Error("No such activity.");
                    index = ActivityTables.ReadDisplays(bytes).GetValueOrDefault(activity.Hash)?.RewardSet ?? -1;
                    if (index < 0) return McpToolResult.Json(new JsonObject { ["activity"] = activity.Name, ["rewards"] = null, ["note"] = "This activity has no client reward set." });
                }
                if (progression && ParseHash(McpJson.String(args, "hash")) is { } wantedHash) index = lists.FindIndex(list => list.Hash == wantedHash);
                if (index >= 0)
                {
                    if (index >= lists.Count) return McpToolResult.Error($"Index out of range (0..{lists.Count - 1}).");
                    return McpToolResult.Json(await RewardJson(data, lists[index], cancel).ConfigureAwait(false));
                }
                var page = DataPages.Page(lists, args, list => new JsonObject
                {
                    ["kind"] = list.Kind, ["index"] = list.Index, ["hash"] = list.Hash == 0 ? null : Hex(list.Hash),
                    ["groups"] = list.Groups.Count, ["items"] = list.Groups.Sum(group => group.Count)
                });
                page["note"] = "Client-side reward lists only; drop tables are rolled by the server.";
                return McpToolResult.Json(page);
            });

        yield return Pkg("get_dialog_table", "A dialog table (class 0x80808D54): its events (definition hash, authored window) and groups of variants, each " +
            "with spoken lines (audio tag, string container, localized text, duration). Without hash it lists dialog tables. Layout fields " +
            "past the arrays are guessed; see get_tag_schema 0x80808D23.",
            Paged(("hash", "string", "Dialog table tag hash", false), ("filter", "string", "Text in the tag name or package (listing only)", false)),
            async (data, args, cancel) =>
            {
                if (ParseHash(McpJson.String(args, "hash")) is not { } hash)
                {
                    string filter = A(args, "filter");
                    var tags = data.TagsOfClass(GameTableTags.DialogTableClass).Where(tag => filter.Length == 0
                        || tag.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true || tag.Package.Contains(filter, StringComparison.OrdinalIgnoreCase));
                    return McpToolResult.Json(DataPages.Page(tags, args, TagRow));
                }
                if (data.Tag(hash) is not { } summary || summary.Class != GameTableTags.DialogTableClass)
                    return McpToolResult.Error("That is not a dialog table tag (class 0x80808D54).");
                if (data.ReadTag(hash, false) is not { } bytes || DialogTables.Read(bytes) is not { } table) return McpToolResult.Error("The dialog table could not be decoded.");
                var groups = new JsonArray();
                foreach (var group in table.Groups)
                {
                    var variants = new JsonArray();
                    foreach (var variant in group.Variants)
                    {
                        var lines = new JsonArray();
                        foreach (var line in variant.Lines)
                            lines.Add(new JsonObject
                            {
                                ["audio"] = Hex(line.Audio), ["strings"] = Hex(line.Strings), ["string_hash"] = Hex(line.StringHash),
                                ["text"] = await Text(data, line.StringHash, cancel).ConfigureAwait(false), ["duration"] = line.Duration
                            });
                        variants.Add(new JsonObject { ["hash"] = Hex(variant.Hash), ["delay"] = variant.Delay, ["weight"] = variant.Weight, ["lines"] = lines });
                    }
                    groups.Add(new JsonObject { ["definition"] = Hex(group.Definition), ["hash"] = Hex(group.Hash), ["variants"] = variants });
                }
                return McpToolResult.Json(new JsonObject
                {
                    ["tag"] = TagRow(summary),
                    ["events"] = new JsonArray([.. table.Events.Select(row => (JsonNode)new JsonObject { ["definition"] = Hex(row.Definition), ["authored_window"] = row.Window })]),
                    ["groups"] = groups,
                    ["guessed"] = "variant delay, weight, line audio and duration are data-only guesses; strings and string_hash come from the client text reader"
                });
            });

        async Task<JsonObject> RewardJson(ISolforgeDataHost data, RewardList list, CancellationToken cancel)
        {
            (uint Hash, uint Tag)[] items = data.ReadTag(GameTableTags.ItemIndex, false) is { } index ? RewardTables.ReadIndex(index) : [];
            (uint Hash, uint Tag)[] displays = data.ReadTag(GameTableTags.ItemDisplayIndex, false) is { } display ? RewardTables.ReadIndex(display) : [];
            var displaySchema = schemas.Find(0x80805CE1);
            var groups = new JsonArray();
            foreach (var group in list.Groups)
            {
                var rows = new JsonArray();
                foreach (var item in group)
                {
                    var row = new JsonObject { ["item_index"] = item.ItemIndex, ["quantity"] = item.Quantity };
                    if (item.Level >= 0) row["level"] = item.Level;
                    if (item.ItemIndex >= 0 && item.ItemIndex < items.Length)
                    {
                        row["item_hash"] = items[item.ItemIndex].Hash;
                        if (displaySchema is not null && item.ItemIndex < displays.Length && displays[item.ItemIndex].Hash == items[item.ItemIndex].Hash
                            && data.ReadTag(displays[item.ItemIndex].Tag, false) is { } shown && displaySchema.Field("name") is { } nameField
                            && nameField.Offset + 8 <= shown.Length)
                            row["name"] = await Text(data, ActivityTables.U32(shown, nameField.Offset + 4), cancel).ConfigureAwait(false);
                    }
                    rows.Add(row);
                }
                groups.Add(rows);
            }
            return new JsonObject { ["kind"] = list.Kind, ["index"] = list.Index, ["hash"] = list.Hash == 0 ? null : Hex(list.Hash), ["groups"] = groups };
        }
    }

    private static JsonObject ActivityRow(ActivityDefinition activity, string? name, ActivityDisplay? shown) => new()
    {
        ["hash"] = Hex(activity.Hash), ["hash_decimal"] = activity.Hash, ["internal_name"] = activity.Name, ["name"] = name,
        ["type"] = activity.Type, ["destination"] = activity.Destination, ["required_level"] = activity.RequiredLevel, ["required_power"] = activity.RequiredPower,
        ["level_2"] = activity.Level2, ["power_2"] = activity.Power2, ["unlock_slot"] = activity.UnlockSlot == ushort.MaxValue ? null : activity.UnlockSlot,
        ["gameplay_settings"] = Hex(activity.GameplaySettings), ["matchmaking_config"] = activity.MatchmakingConfig is { } config ? Hex(config) : null,
        ["has_movie"] = activity.HasMovie, ["reward_set"] = shown is { RewardSet: >= 0 } ? shown.RewardSet : null
    };
}
