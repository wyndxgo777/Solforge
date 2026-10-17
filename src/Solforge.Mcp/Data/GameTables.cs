using System.Buffers.Binary;
using SunriseStudio.Packages.TagSchemas;

namespace Solforge.Mcp.Data;

/** Tags and classes of the read-only game tables (activities, reward lists, dialog). */
public static class GameTableTags
{
    public const uint ActivityDefinitions = 0x81327CF0, ActivityDisplays = 0x81327D35, Progressions = 0x8131933C;
    public const uint ItemIndex = 0x81327CCB, ItemDisplayIndex = 0x81613CF1;
    public const uint ActivityDefinitionClass = 0x808076F0, ActivityDisplayClass = 0x80805E0A, ActivityRootClass = 0x80808AAE;
    public const uint ProgressionClass = 0x80807CD9, DialogTableClass = 0x80808D54;
}

/** One activity definition record (offsets from the "activity_definition" schema). */
public sealed record ActivityDefinition(int Index, uint Hash, string Name, int RequiredLevel, int RequiredPower, int Level2, int Power2,
    ushort UnlockSlot, byte Type, uint GameplaySettings, byte Destination, uint? MatchmakingConfig, bool HasMovie, int Offset);

/** The display side of an activity: localized name and description (bank, hash) and its reward set index (-1 = none). */
public sealed record ActivityDisplay(uint Hash, uint NameBank, uint NameHash, uint DescriptionBank, uint DescriptionHash, int RewardSet, int Offset);

/** A per-activity root tag (class 0x80808AAE): FNV-1 hash of the activity name and the tags it pulls in. */
public sealed record ActivityRoot(uint NameHash, IReadOnlyList<uint> Parts);

/**
 * Decodes the activity definition table (0x81327CF0), the activity display table (0x81327D35) and the per-activity root
 * tags. Every offset comes from the tag schema registry, so a corrected schema file fixes this decoder too.
 */
public static class ActivityTables
{
    /** All definition records in index order; rows whose record pointer is bad are skipped. */
    public static List<ActivityDefinition> ReadDefinitions(byte[] bytes, TagSchemaRegistry? schemas = null)
    {
        schemas ??= TagSchemaRegistry.Default;
        var table = schemas.Require(GameTableTags.ActivityDefinitionClass);
        var row = schemas.Require(0x808076FCu);
        var record = schemas.Require("activity_definition");
        var rows = new List<ActivityDefinition>();
        if (!TagSchemaReader.TryArray(bytes, table.Offset("table"), out var array, out _)) return rows;
        int stride = table.Field("table")!.Stride, pointer = row.Offset("record");
        int levelAt = record.Offset("required_level"), powerAt = record.Offset("required_power"), level2At = record.Offset("level_2"),
            power2At = record.Offset("power_2"), slotAt = record.Offset("unlock_slot"), typeAt = record.Offset("activity_type"),
            settingsAt = record.Offset("gameplay_settings"), destinationAt = record.Offset("destination"), nameAt = record.Offset("internal_name"),
            configAt = record.Offset("matchmaking_config"), movieAt = record.Offset("movie_route"), hashAt = record.Offset("hash");
        for (int i = 0; i < array.Count; i++)
        {
            int at = array.Data + i * stride;
            if (at + stride > bytes.Length || !TagSchemaReader.TryRelative(bytes, at + pointer, out int start) || start + destinationAt + 1 > bytes.Length) continue;
            string name = TagSchemaReader.TryRelative(bytes, start + nameAt, out int text) ? TagSchemaReader.CString(bytes, text) ?? "" : "";
            uint? config = TagSchemaReader.TryRelative(bytes, start + configAt, out int configTag) && configTag + 4 <= bytes.Length
                && U32(bytes, configTag) is var value && value != uint.MaxValue ? value : null;
            rows.Add(new(i, U32(bytes, start + hashAt), name, I32(bytes, start + levelAt), I32(bytes, start + powerAt), I32(bytes, start + level2At),
                I32(bytes, start + power2At), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(start + slotAt)), bytes[start + typeAt],
                U32(bytes, start + settingsAt), bytes[start + destinationAt], config, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(start + movieAt)) != 0, start));
        }
        return rows;
    }

    /** Display rows by activity hash. */
    public static Dictionary<uint, ActivityDisplay> ReadDisplays(byte[] bytes, TagSchemaRegistry? schemas = null)
    {
        schemas ??= TagSchemaRegistry.Default;
        var table = schemas.Require(GameTableTags.ActivityDisplayClass);
        var row = schemas.Require(0x80805E15u);
        var record = schemas.Require(0x80805E18u);
        var display = schemas.Require(0x80805E23u);
        var result = new Dictionary<uint, ActivityDisplay>();
        if (!TagSchemaReader.TryArray(bytes, table.Offset("table"), out var array, out _)) return result;
        int stride = table.Field("table")!.Stride, pointer = row.Offset("record"), rowPointer = record.Offset("display_row"),
            rewardAt = record.Offset("reward_set_index"), nameAt = display.Offset("name"), descriptionAt = display.Offset("description");
        for (int i = 0; i < array.Count; i++)
        {
            int at = array.Data + i * stride;
            if (at + stride > bytes.Length || !TagSchemaReader.TryRelative(bytes, at + pointer, out int start) || start + rewardAt + 2 > bytes.Length) continue;
            uint hash = U32(bytes, at + row.Offset("hash"));
            ushort reward = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(start + rewardAt));
            if (!TagSchemaReader.TryRelative(bytes, start + rowPointer, out int text) || text + display.Size > bytes.Length)
            { result[hash] = new(hash, 0, 0, 0, 0, reward == ushort.MaxValue ? -1 : reward, start); continue; }
            result[hash] = new(hash, U32(bytes, text + nameAt), U32(bytes, text + nameAt + 4), U32(bytes, text + descriptionAt),
                U32(bytes, text + descriptionAt + 4), reward == ushort.MaxValue ? -1 : reward, start);
        }
        return result;
    }

    /** A per-activity root tag, or null when the bytes are not one. */
    public static ActivityRoot? ReadRoot(byte[] bytes, TagSchemaRegistry? schemas = null)
    {
        schemas ??= TagSchemaRegistry.Default;
        var root = schemas.Require(GameTableTags.ActivityRootClass);
        int hashAt = root.Offset("name_hash");
        if (bytes.Length < hashAt + 4 || !TagSchemaReader.TryArray(bytes, root.Offset("parts"), out var parts, out _)) return null;
        int stride = root.Field("parts")!.Stride;
        var tags = new List<uint>();
        for (int i = 0; i < parts.Count && parts.Data + (i + 1) * stride <= bytes.Length; i++) tags.Add(U32(bytes, parts.Data + i * stride));
        return new(U32(bytes, hashAt), tags);
    }

    /** FNV-1 32 (multiply, then xor), the hash activity roots use for their names. */
    public static uint Fnv1(string text)
    {
        uint hash = 0x811C9DC5;
        foreach (char c in text) { hash *= 0x01000193; hash ^= (byte)c; }
        return hash;
    }

    internal static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    internal static int I32(byte[] bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
}

/** One reward line: an item index (row of the item index tag 0x81327CCB) and a quantity. */
public sealed record RewardItem(int ItemIndex, long Quantity, int Level = -1);

/** A reward list: activity reward sets hold groups of items; progressions hold items with the level that grants them. */
public sealed record RewardList(string Kind, int Index, uint Hash, IReadOnlyList<IReadOnlyList<RewardItem>> Groups);

/**
 * The reward lists the client ships. Loot tables proper are rolled by the server; the client has the activity reward
 * sets (display table 0x81327D35, field reward_sets) and the progression reward_items (0x8131933C).
 */
public static class RewardTables
{
    /** Activity reward sets in table order; each holds groups of reward items. */
    public static List<RewardList> ReadActivityRewardSets(byte[] bytes, TagSchemaRegistry? schemas = null)
    {
        schemas ??= TagSchemaRegistry.Default;
        var table = schemas.Require(GameTableTags.ActivityDisplayClass);
        var set = schemas.Require(0x80805E3Eu);
        var group = schemas.Require(0x80805E40u);
        var item = schemas.Require(0x80805E42u);
        var result = new List<RewardList>();
        if (!TagSchemaReader.TryArray(bytes, table.Offset("reward_sets"), out var sets, out _)) return result;
        int setStride = table.Field("reward_sets")!.Stride, groupStride = set.Field("groups")!.Stride, itemStride = group.Field("reward_items")!.Stride;
        for (int i = 0; i < sets.Count; i++)
        {
            var groups = new List<IReadOnlyList<RewardItem>>();
            if (TagSchemaReader.TryArray(bytes, sets.Data + i * setStride + set.Offset("groups"), out var groupArray, out _))
                for (int g = 0; g < groupArray.Count; g++)
                {
                    var items = new List<RewardItem>();
                    int groupAt = groupArray.Data + g * groupStride;
                    if (TagSchemaReader.TryArray(bytes, groupAt + group.Offset("reward_items"), out var itemArray, out _))
                        for (int k = 0; k < itemArray.Count && itemArray.Data + (k + 1) * itemStride <= bytes.Length; k++)
                        {
                            int at = itemArray.Data + k * itemStride;
                            items.Add(new(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + item.Offset("item_index"))), ActivityTables.U32(bytes, at + item.Offset("quantity"))));
                        }
                    groups.Add(items);
                }
            result.Add(new("activity_reward_set", i, 0, groups));
        }
        return result;
    }

    /** Progression definitions (0x8131933C) with their reward_items, one group per progression. */
    public static List<RewardList> ReadProgressions(byte[] bytes, TagSchemaRegistry? schemas = null)
    {
        schemas ??= TagSchemaRegistry.Default;
        var table = schemas.Require(GameTableTags.ProgressionClass);
        var progression = schemas.Require(0x80807CDDu);
        var reward = schemas.Require(0x80802FF7u);
        var result = new List<RewardList>();
        if (!TagSchemaReader.TryArray(bytes, table.Offset("table"), out var rows, out _)) return result;
        int stride = table.Field("table")!.Stride, rewardStride = progression.Field("reward_items")!.Stride;
        for (int i = 0; i < rows.Count && rows.Data + (i + 1) * stride <= bytes.Length; i++)
        {
            int at = rows.Data + i * stride;
            var items = new List<RewardItem>();
            if (TagSchemaReader.TryArray(bytes, at + progression.Offset("reward_items"), out var rewards, out _))
                for (int k = 0; k < rewards.Count && rewards.Data + (k + 1) * rewardStride <= bytes.Length; k++)
                {
                    int r = rewards.Data + k * rewardStride;
                    items.Add(new((int)ActivityTables.U32(bytes, r + reward.Offset("item_index")), ActivityTables.I32(bytes, r + reward.Offset("quantity")),
                        ActivityTables.I32(bytes, r + reward.Offset("rewarded_at_level"))));
                }
            result.Add(new("progression", i, ActivityTables.U32(bytes, at + progression.Offset("hash")), [items]));
        }
        return result;
    }

    /** (hash, tag) rows of an investment index tag (item index, display index), from the investment_index_row schema. */
    public static (uint Hash, uint Tag)[] ReadIndex(byte[] bytes, TagSchemaRegistry? schemas = null)
    {
        schemas ??= TagSchemaRegistry.Default;
        var row = schemas.Require("investment_index_row");
        if (!TagSchemaReader.TryArray(bytes, 8, out var array, out _)) return [];
        var result = new List<(uint, uint)>(array.Count);
        for (int i = 0; i < array.Count && array.Data + (i + 1) * row.Size <= bytes.Length; i++)
        {
            int at = array.Data + i * row.Size;
            result.Add((ActivityTables.U32(bytes, at + row.Offset("hash")), ActivityTables.U32(bytes, at + row.Offset("tag"))));
        }
        return [.. result];
    }
}

/** One spoken line: audio tag, string container tag, localized string hash and duration in seconds. */
public sealed record DialogLine(uint Audio, uint Strings, uint StringHash, float Duration);
/** One variant (alternative take) of a dialog group. */
public sealed record DialogVariant(uint Hash, float Delay, float Weight, IReadOnlyList<DialogLine> Lines, int Offset);
/** A dialog group: the definition it plays for and its variants. */
public sealed record DialogGroup(uint Definition, uint Hash, IReadOnlyList<DialogVariant> Variants);
/** A decoded dialog table (class 0x80808D54). */
public sealed record DialogTable(IReadOnlyList<(uint Definition, float Window)> Events, IReadOnlyList<DialogGroup> Groups);

/** Decodes dialog tables: events (definition hash + authored window) and groups of variants of spoken lines. */
public static class DialogTables
{
    public static DialogTable? Read(byte[] bytes, TagSchemaRegistry? schemas = null)
    {
        schemas ??= TagSchemaRegistry.Default;
        var table = schemas.Require(GameTableTags.DialogTableClass);
        var definition = schemas.Require(0x80808D18u);
        var group = schemas.Require(0x80808D19u);
        var content = schemas.Require(0x80808D1Du);
        var pointer = schemas.Require(0x80808D20u);
        var variant = schemas.Require(0x80808D23u);
        if (!TagSchemaReader.TryArray(bytes, table.Offset("events"), out var events, out _)
            || !TagSchemaReader.TryArray(bytes, table.Offset("groups"), out var groups, out _)) return null;
        var eventRows = new List<(uint, float)>();
        for (int i = 0; i < events.Count && events.Data + (i + 1) * definition.Size <= bytes.Length; i++)
        {
            int at = events.Data + i * definition.Size;
            eventRows.Add((ActivityTables.U32(bytes, at + definition.Offset("definition_hash")),
                BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + definition.Offset("authored_window")))));
        }
        string[] lineNames = ["line1", "line2"];
        var groupRows = new List<DialogGroup>();
        for (int i = 0; i < groups.Count && groups.Data + (i + 1) * group.Size <= bytes.Length; i++)
        {
            int at = groups.Data + i * group.Size;
            uint definitionHash = ActivityTables.U32(bytes, at + group.Offset("definition_hash"));
            var variants = new List<DialogVariant>();
            uint contentHash = 0;
            if (TagSchemaReader.TryRelative(bytes, at + group.Offset("content"), out int body) && body + content.Size <= bytes.Length)
            {
                contentHash = ActivityTables.U32(bytes, body + content.Offset("hash"));
                if (TagSchemaReader.TryArray(bytes, body + content.Offset("variants"), out var list, out _))
                    for (int v = 0; v < list.Count && list.Data + (v + 1) * pointer.Size <= bytes.Length; v++)
                    {
                        if (!TagSchemaReader.TryRelative(bytes, list.Data + v * pointer.Size + pointer.Offset("variant"), out int start)
                            || start + variant.Size > bytes.Length) continue;
                        var lines = new List<DialogLine>();
                        foreach (string line in lineNames)
                        {
                            uint audio = ActivityTables.U32(bytes, start + variant.Offset(line + "_audio"));
                            uint strings = ActivityTables.U32(bytes, start + variant.Offset(line + "_strings"));
                            uint text = ActivityTables.U32(bytes, start + variant.Offset(line + "_string"));
                            if (audio is 0 or uint.MaxValue && strings is 0 or uint.MaxValue) continue;
                            lines.Add(new(audio, strings, text, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(start + variant.Offset(line + "_duration")))));
                        }
                        variants.Add(new(ActivityTables.U32(bytes, start + variant.Offset("hash")),
                            BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(start + variant.Offset("delay"))),
                            BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(start + variant.Offset("weight"))), lines, start));
                    }
            }
            groupRows.Add(new(definitionHash, contentHash, variants));
        }
        return new(eventRows, groupRows);
    }
}
