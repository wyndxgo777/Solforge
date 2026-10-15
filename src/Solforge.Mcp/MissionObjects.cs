using System.Text;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/**
 * One placed object of the open mission: an authored prefab slot ('pf_event_multipoint.pf_placement[0].o_generator',
 * 'slot/80be91f7/000014/0014/0004') and, when the map places it, the marker or squad that does.
 */
public sealed record MissionSlot(string SlotId, string Name, uint RegistryKey, int Type, int Index, uint PrefabTag)
{
    /** The world object (marker, squad) that places the slot; select_object and focus_camera take it. */
    public string? ObjectId { get; init; }
    public string? Label { get; init; }
    public string? Kind { get; init; }
    public uint AssetTag { get; init; }
    public string? Zone { get; init; }
    public float[]? Position { get; init; }
    public float[]? Rotation { get; init; }
    public float[]? Extents { get; init; }
    /** The prefab's own row in the Object tree and its name. */
    public string? PrefabObjectId { get; init; }
    public string? PrefabName { get; init; }
    /** Every world object bound to the slot (capped). */
    public IReadOnlyList<string> Placements { get; init; } = [];
}

/** The open mission's slots, indexed by slot id, world object id, name and parent; immutable, so tools read it on any thread. */
public sealed class MissionSnapshot
{
    private readonly Dictionary<string, MissionSlot> byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MissionSlot> byObject = new(StringComparer.Ordinal);
    private readonly ILookup<string, MissionSlot> byName;
    private readonly ILookup<(uint, string), MissionSlot> byParent;

    public MissionSnapshot(string? map, uint missionTag, string? missionName, IEnumerable<MissionSlot> slots)
    {
        Map = map; MissionTag = missionTag; MissionName = missionName;
        Slots = slots.OrderBy(slot => slot.Name, StringComparer.Ordinal).ThenBy(slot => slot.SlotId, StringComparer.Ordinal).ToArray();
        foreach (var slot in Slots)
        {
            byId.TryAdd(slot.SlotId, slot);
            foreach (string placed in slot.Placements) byObject.TryAdd(placed, slot);
            if (slot.ObjectId is { } id) byObject.TryAdd(id, slot);
        }
        byName = Slots.ToLookup(slot => slot.Name, StringComparer.OrdinalIgnoreCase);
        byParent = Slots.Where(slot => MissionSlots.ParentName(slot.Name) is not null)
            .ToLookup(slot => (slot.RegistryKey, MissionSlots.ParentName(slot.Name)!));
    }

    public string? Map { get; }
    public uint MissionTag { get; }
    public string? MissionName { get; }
    public IReadOnlyList<MissionSlot> Slots { get; }

    /** A slot id, a placing world object id, an exact slot name, or an SDK constant such as PF_..._O_GENERATOR[_80BE5D9B]. */
    public MissionSlot? Find(string id)
    {
        id = id.Trim();
        if (id.Length == 0) return null;
        if (byId.TryGetValue(id, out var slot) || byObject.TryGetValue(id, out slot)) return slot;
        if (byName[id].FirstOrDefault() is { } named) return named;
        uint? registry = null;
        string constant = id;
        if (id.Length > 9 && id[^9] == '_' && uint.TryParse(id[^8..], System.Globalization.NumberStyles.HexNumber, null, out uint suffix))
        { registry = suffix; constant = id[..^9]; }
        return Slots.FirstOrDefault(each => (registry is null || each.RegistryKey == registry)
            && string.Equals(MissionSlots.ConstantName(each.Name), constant, StringComparison.OrdinalIgnoreCase));
    }

    public MissionSlot? Parent(MissionSlot slot) => MissionSlots.ParentName(slot.Name) is { } parent
        ? byName[parent].FirstOrDefault(each => each.RegistryKey == slot.RegistryKey) : null;

    public IReadOnlyList<MissionSlot> Children(MissionSlot slot) => byParent[(slot.RegistryKey, slot.Name)].ToArray();

    /** Slots of the same prefab whose names sit under this one (any depth), for prefabs whose parent has no slot of its own. */
    public IEnumerable<MissionSlot> Descendants(MissionSlot slot) => Slots.Where(each => each.RegistryKey == slot.RegistryKey
        && each.Name.Length > slot.Name.Length && each.Name.StartsWith(slot.Name + ".", StringComparison.Ordinal));

    /** find_object rows: label is the slot name; the slot id, registry, prefab and asset hashes resolve too. */
    public IEnumerable<ObjectEntry> Entries(IReadOnlySet<string>? visible = null) => Slots.Select(slot => new ObjectEntry(slot.SlotId, slot.Name,
        "Mission slot" + (slot.ObjectId is null ? "" : " (placed)") + (slot.Kind is { } kind ? " " + kind : ""),
        slot.ObjectId is { } placed && visible?.Contains(placed) == true ? ObjectScope.Viewport : ObjectScope.Tree,
        Map, null, slot.PrefabTag, [slot.RegistryKey, slot.AssetTag]));
}

public static class MissionSlots
{
    /** The id the SDK mission Lua uses: slot/{registry}/{index:6}/{index:4}/{type:4}, lower-case hex. */
    public static string SlotId(uint registryKey, int type, int index) => $"slot/{registryKey:x8}/{index:x6}/{index:x4}/{type:x4}";

    public static bool TryParseSlotId(string id, out uint registryKey, out int type, out int index)
    {
        registryKey = 0; type = 0; index = 0;
        string[] parts = id.Trim().Split('/');
        const System.Globalization.NumberStyles Hex = System.Globalization.NumberStyles.HexNumber;
        return parts.Length == 5 && parts[0].Equals("slot", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(parts[1], Hex, null, out registryKey) && int.TryParse(parts[3], Hex, null, out index)
            && int.TryParse(parts[4], Hex, null, out type);
    }

    /** 'a.b[0].c' -> 'a.b[0]'; null at the top. Dots inside brackets do not split. */
    public static string? ParentName(string name)
    {
        int depth = 0;
        for (int i = name.Length - 1; i > 0; i--)
        {
            char c = name[i];
            if (c == ']') depth++;
            else if (c == '[') depth--;
            else if (c == '.' && depth == 0) return name[..i];
        }
        return null;
    }

    /** The SDK table key: 'pf_x.pf_placement[0].o_ring' -> 'PF_X_PF_PLACEMENT_0_O_RING'. Server scripts add '_{REGISTRY}'. */
    public static string ConstantName(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsAsciiLetterOrDigit(c)) text.Append(char.ToUpperInvariant(c));
            else if (text.Length > 0 && text[^1] != '_') text.Append('_');
        }
        return text.ToString().TrimEnd('_');
    }

    public static JsonObject Row(MissionSlot slot)
    {
        var row = new JsonObject
        {
            ["slotId"] = slot.SlotId, ["name"] = slot.Name, ["type"] = slot.Type, ["index"] = slot.Index,
            ["registry"] = $"0x{slot.RegistryKey:X8}", ["prefab"] = slot.PrefabTag != 0 ? $"0x{slot.PrefabTag:X8}" : null,
            ["constant"] = ConstantName(slot.Name)
        };
        if (slot.PrefabName is { } prefabName) row["prefabName"] = prefabName;
        if (slot.ObjectId is { } id) row["objectId"] = id;
        if (slot.Label is { } label && label != slot.Name) row["label"] = label;
        if (slot.Kind is { } kind) row["kind"] = kind;
        if (slot.AssetTag != 0) row["tag"] = $"0x{slot.AssetTag:X8}";
        if (slot.Zone is { Length: > 0 } zone) row["zone"] = zone;
        if (slot.Position is { } position) row["position"] = Floats(position);
        return row;
    }

    /** get_mission_object without code refs: the row, transform, parent prefab, parent slot and a page of children. */
    public static JsonObject Describe(MissionSnapshot snapshot, MissionSlot slot, int childOffset, int childLimit)
    {
        var details = Row(slot);
        details["mission"] = snapshot.MissionTag != 0 ? $"0x{snapshot.MissionTag:X8}" : null;
        details["missionName"] = snapshot.MissionName;
        details["transform"] = new JsonObject
        {
            ["position"] = slot.Position is { } p ? Floats(p) : null, ["rotation"] = slot.Rotation is { } r ? Floats(r) : null,
            ["extents"] = slot.Extents is { } e ? Floats(e) : null
        };
        details["prefabObject"] = slot.PrefabObjectId is null ? null
            : new JsonObject { ["id"] = slot.PrefabObjectId, ["name"] = slot.PrefabName, ["tag"] = slot.PrefabTag != 0 ? $"0x{slot.PrefabTag:X8}" : null };
        if (ParentName(slot.Name) is { } parentName)
            details["parent"] = snapshot.Parent(slot) is { } parent ? Row(parent) : new JsonObject { ["name"] = parentName, ["note"] = "no slot of its own" };
        var direct = snapshot.Children(slot);
        // A grouping name (pf_placement[0]) often has no slot; then everything beneath it is its child.
        IReadOnlyList<MissionSlot> children = direct.Count > 0 ? direct : snapshot.Descendants(slot).ToArray();
        details["children"] = Pages.Page(children, childOffset, childLimit, Row);
        var placements = new JsonArray();
        foreach (string id in slot.Placements.Take(16)) placements.Add(id);
        details["placements"] = placements;
        return details;
    }

    /** list_mission_objects: slots whose name, id, constant, kind or label contain every word of the filter. */
    public static JsonObject List(MissionSnapshot snapshot, string? filter, bool placedOnly, int offset, int limit)
    {
        string[] words = (filter ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var rows = snapshot.Slots.Where(slot => (!placedOnly || slot.ObjectId is not null) && words.All(word =>
            slot.Name.Contains(word, StringComparison.OrdinalIgnoreCase) || slot.SlotId.Contains(word, StringComparison.OrdinalIgnoreCase)
            || ConstantName(slot.Name).Contains(word, StringComparison.OrdinalIgnoreCase)
            || (slot.Kind?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false)
            || (slot.Label?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false)
            || (slot.PrefabName?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false)
            || $"{slot.PrefabTag:x8} {slot.RegistryKey:x8}".Contains(word.Replace("0x", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase)));
        var page = Pages.Page(rows, offset, limit, Row);
        page["map"] = snapshot.Map;
        page["mission"] = snapshot.MissionTag != 0 ? $"0x{snapshot.MissionTag:X8}" : null;
        page["missionName"] = snapshot.MissionName;
        return page;
    }

    private static JsonArray Floats(float[] values)
    {
        var array = new JsonArray();
        foreach (float value in values) array.Add(Math.Round(value, 3));
        return array;
    }
}

/** Offset/limit pages with a total and the next offset, so big lists (EDZ) never come back whole. */
public static class Pages
{
    public const int MaximumLimit = 500;

    public static JsonObject Page<T>(IEnumerable<T> rows, int offset, int limit, Func<T, JsonNode?> row)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, MaximumLimit);
        var items = new JsonArray();
        int total = 0;
        foreach (var item in rows)
        {
            if (total >= offset && items.Count < limit) items.Add(row(item));
            total++;
        }
        return new JsonObject
        {
            ["total"] = total, ["offset"] = offset, ["count"] = items.Count,
            ["next"] = offset + items.Count < total ? (int?)(offset + items.Count) : null, ["rows"] = items
        };
    }
}
