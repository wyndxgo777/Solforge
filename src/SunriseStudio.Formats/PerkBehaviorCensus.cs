using System.Buffers.Binary;
using System.Collections;
using SunriseStudio.Packages;

namespace SunriseStudio.Formats;

/** One native node kind installed perk programs use; a kind is its class and opcode. */
public sealed record PerkInstalledKind(PerkBehaviorNodeKind Kind, uint NativeClass, byte Opcode, int Nodes, int Programs);

/** Installed node kinds, plus the installed values the editor's pickers offer. */
public sealed class PerkCensus(IReadOnlyList<PerkInstalledKind> kinds) : IReadOnlyList<PerkInstalledKind>
{
    /** Event hashes installed event conditions test, nested ones included. */
    public IReadOnlyList<PerkBehaviorChoice> EventHashes { get; init; } = [];
    /** Keys of the installed transmat effect table. */
    public IReadOnlyList<PerkBehaviorChoice> TransmatKeys { get; init; } = [];
    /** Object definitions a spawn effect can create; installed spawn targets come first. */
    public IReadOnlyList<PerkBehaviorChoice> ObjectDefinitions { get; init; } = [];
    /** Other installed value lists, keyed by the picker name a property carries. */
    public IReadOnlyDictionary<string, IReadOnlyList<PerkBehaviorChoice>> Values { get; init; } =
        new Dictionary<string, IReadOnlyList<PerkBehaviorChoice>>();

    /** The installed label table, for label filters; null when the tag is missing. */
    public PerkLabelTable? LabelTable { get; init; }

    /** The installed values for a property's picker; empty when the census has none. */
    public IReadOnlyList<PerkBehaviorChoice> Picker(string name) => name switch
    {
        "event" => EventHashes, "transmat" => TransmatKeys, "definitions" => ObjectDefinitions,
        _ => Values.GetValueOrDefault(name) ?? []
    };

    public int Count => kinds.Count;
    public PerkInstalledKind this[int index] => kinds[index];
    /** Enumerates the installed node kinds. */
    public IEnumerator<PerkInstalledKind> GetEnumerator() => kinds.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/** Counts the node kinds of every installed perk program, so the editor can list the ones it cannot write. */
public static class PerkBehaviorCensus
{
    /** Spawn effects create objects of this class. */
    public const uint ObjectDefinitionClass = 0x80809C0F;
    // Behavior scripts and component configs are the other tag classes effects reference.
    private const uint BehaviorClass = 0x8080941E, ConfigClass = 0x80809C36;
    // Rows {u32 key, u8 mask, u32 object definition} at +64; transmat effects select one by key.
    private const uint TransmatTableTag = 0x80B9E5C4, TransmatRowClass = 0x808064A2;
    private const uint EmptyHash = InvestmentItemClone.EmptyStringHash;
    private const uint CounterClass = 0x80803E30, GatedClass = 0x80803DCC, AllGroupsClass = 0x80803E04;
    private const uint ExtendTimersClass = 0x80803E3B, TransmatClass = 0x80803E2E, ScaleClass = 0x808029F2;

    /** Payload fields whose installed values feed a picker: (class, offset, picker). */
    private static readonly (uint Class, int Offset, string Picker)[] Sources =
    [
        (0x80803DEB, 8, "event"), (0x80803DEC, 8, "event29"), (0x80803DEA, 8, "incident"), (0x80803DEA, 12, "role"),
        (0x80802D02, 8, "tracked"), (0x80802D01, 8, "tracked"), (0x80802D0D, 4, "tracked"), (0x80802D00, 8, "message"),
        (0x80803E1D, 4, "modifier"), (0x80803E1E, 4, "sim"), (0x808029F1, 4, "stat"), (0x80803E1C, 4, "channel"),
        (TransmatClass, 4, "transmat"),
        (0x80803E45, 16, "definitions"), (0x80803E44, 16, "definitions"), (0x80803E43, 16, "definitions"),
        (0x80803E42, 24, "definitions"), (0x80803E47, 16, "definitions"), (0x80803E12, 16, "definitions"),
        (0x80803DFF, 16, "definitions"), (0x80803DFE, 16, "definitions"), (0x80803DE4, 16, "definitions"),
        (0x80802D0A, 16, "behaviors"), (0x80803E46, 16, "configs")
    ];

    /** Scans every perk behavior in the packages and counts the nodes and programs of each kind. */
    public static PerkCensus Read(PackageCatalog catalog, CancellationToken token = default)
    {
        var nodes = new Dictionary<(PerkBehaviorNodeKind, uint, byte), (int Nodes, HashSet<uint> Programs)>();
        var uses = Sources.Select(source => source.Picker).Append("scaled").Distinct()
            .ToDictionary(name => name, _ => new Dictionary<uint, HashSet<uint>>());
        foreach (uint tag in catalog.Entries.Where(entry => entry.Reference == NativePerkBehavior.Class && entry.Type is 8 or 16)
            .Select(entry => entry.TagHash).Distinct())
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes;
            try { bytes = catalog.Read(tag); } catch (Exception error) when (error is IOException or InvalidDataException) { continue; }
            foreach (var (node, kind) in Walk(bytes))
            {
                uint type = U32(bytes, node - 4);
                var key = (kind, type, bytes[node + (kind == PerkBehaviorNodeKind.Condition ? 5 : 0)]);
                if (!nodes.TryGetValue(key, out var row)) nodes[key] = row = (0, []);
                row.Programs.Add(tag);
                nodes[key] = (row.Nodes + 1, row.Programs);
                var found = Sources.Where(source => source.Class == type && node + source.Offset + 4 <= bytes.Length)
                    .Select(source => (source.Picker, Value: U32(bytes, node + source.Offset)));
                // Component scale effects hold their names in a u32 array at +8.
                if (type == ScaleClass && node + 24 <= bytes.Length)
                    found = found.Concat(Rows(bytes, node + 8, 4).Select(row => ("scaled", U32(bytes, row))));
                foreach (var (picker, value) in found)
                {
                    if (value is EmptyHash or uint.MaxValue) continue;
                    var users = uses[picker];
                    if (!users.TryGetValue(value, out var programs)) users[value] = programs = [];
                    programs.Add(tag);
                }
            }
        }
        var kinds = nodes.Select(pair => new PerkInstalledKind(pair.Key.Item1, pair.Key.Item2, pair.Key.Item3,
                pair.Value.Nodes, pair.Value.Programs.Count))
            .OrderBy(kind => kind.Kind).ThenByDescending(kind => kind.Programs).ToArray();
        var values = uses.Where(pair => pair.Key is not ("event" or "transmat" or "definitions" or "behaviors" or "configs"))
            .ToDictionary(pair => pair.Key, pair => Installed(pair.Value));
        values["behaviors"] = Tags(catalog, BehaviorClass, uses["behaviors"]);
        values["configs"] = Tags(catalog, ConfigClass, uses["configs"]);
        var labels = LabelTable(catalog);
        // A label value packs the label's bit above its hash, so a pick sets both.
        values["labels"] = [.. (labels?.Entries ?? []).Where(entry => !entry.Group)
            .Select(entry => new PerkBehaviorChoice(entry.Bit * 4294967296.0 + entry.Hash, $"{entry.Name} (bit {entry.Bit})"))];
        return new PerkCensus(kinds)
        {
            EventHashes = Installed(uses["event"]),
            TransmatKeys = TransmatTable(catalog, uses["transmat"]),
            ObjectDefinitions = Tags(catalog, ObjectDefinitionClass, uses["definitions"]),
            LabelTable = labels,
            Values = values
        };
    }

    /** The installed label table; null when the tag is missing or unreadable. */
    private static PerkLabelTable? LabelTable(PackageCatalog catalog)
    {
        if (!catalog.ContainsTag(PerkLabelTable.Tag)) return null;
        try { return PerkLabelTable.Parse(catalog.Read(PerkLabelTable.Tag)); }
        catch (Exception error) when (error is IOException or InvalidDataException) { return null; }
    }

    private static IReadOnlyList<PerkBehaviorChoice> Installed(Dictionary<uint, HashSet<uint>> uses) =>
        [.. uses.OrderByDescending(pair => pair.Value.Count).ThenBy(pair => pair.Key)
            .Select(pair => new PerkBehaviorChoice(pair.Key, $"0x{pair.Key:X8} ({Perks(pair.Value.Count)})"))];

    /** Every installed tag of a class; tags installed perks use come first. */
    private static IReadOnlyList<PerkBehaviorChoice> Tags(PackageCatalog catalog, uint type, Dictionary<uint, HashSet<uint>> uses) =>
        [.. catalog.Entries.Where(entry => entry.Reference == type).Select(entry => entry.TagHash).Distinct()
            .OrderByDescending(tag => uses.GetValueOrDefault(tag)?.Count ?? 0).ThenBy(tag => tag)
            .Select(tag => new PerkBehaviorChoice(tag, Label(catalog, tag)
                + (uses.TryGetValue(tag, out var users) ? $" ({Perks(users.Count)})" : "")))];

    private static IReadOnlyList<PerkBehaviorChoice> TransmatTable(PackageCatalog catalog, Dictionary<uint, HashSet<uint>> users)
    {
        if (!catalog.ContainsTag(TransmatTableTag)) return [];
        byte[] bytes;
        try { bytes = catalog.Read(TransmatTableTag); } catch (Exception error) when (error is IOException or InvalidDataException) { return []; }
        var data = new TagReader(bytes);
        if (!data.TryArray(64, TransmatRowClass, 12, out var rows)) return [];
        return [.. Enumerable.Range(0, rows.Count).Select(index => rows.Data + index * 12)
            .Select(row => (Key: data.U32(row), Definition: data.U32(row + 8)))
            .Select(row => new PerkBehaviorChoice(row.Key, $"0x{row.Key:X8} -> {Label(catalog, row.Definition)}"
                + (users.TryGetValue(row.Key, out var programs) ? $" ({Perks(programs.Count)})" : "")))];
    }

    /** Every node offset in a program: phases, stack branches, rows, groups, inners and timer conditions. */
    private static IEnumerable<(int Node, PerkBehaviorNodeKind Kind)> Walk(byte[] bytes)
    {
        var found = new List<(int, PerkBehaviorNodeKind)>();
        var seen = new HashSet<int>();
        foreach (int field in new[] { 32, 72, 88 })
            foreach (int node in Nodes(bytes, field, 8)) Condition(node, 0);
        foreach (int node in Nodes(bytes, 56, 8)) Effect(node);
        if (bytes.Length >= 120 && BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(104)) is > 0 and < 4096)
        {
            long count = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(104));
            long rows = 112 + BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(112)) + 16;
            for (int i = 0; i < count && rows + i * 72 + 72 <= bytes.Length; i++)
            {
                int row = (int)(rows + i * 72);
                foreach (int offset in new[] { 0, 40, 56 })
                    foreach (int node in Nodes(bytes, row + offset, 8)) Condition(node, 0);
                foreach (int node in Nodes(bytes, row + 24, 8)) Effect(node);
            }
        }
        return found;

        void Effect(int node)
        {
            if (!seen.Add(node)) return;
            found.Add((node, PerkBehaviorNodeKind.Effect));
            if (node + 40 <= bytes.Length && U32(bytes, node - 4) == ExtendTimersClass)
                foreach (int child in Nodes(bytes, node + 16, 8)) Condition(child, 1);
        }

        void Condition(int node, int depth)
        {
            if (depth > 8 || node + 8 > bytes.Length || !seen.Add(node)) return;
            found.Add((node, PerkBehaviorNodeKind.Condition));
            uint type = U32(bytes, node - 4);
            if (type == CounterClass && node + 56 <= bytes.Length)
                foreach (int child in Nodes(bytes, node + 8, 32)) Condition(child, depth + 1);
            if (type == GatedClass && node + 264 <= bytes.Length)
            {
                long inner = node + 256 + BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(node + 256));
                if (inner >= 4 && inner < bytes.Length) Condition((int)inner, depth + 1);
            }
            if (type == AllGroupsClass && node + 24 <= bytes.Length)
                foreach (int group in Rows(bytes, node + 8, 32))
                    foreach (int child in Nodes(bytes, group + 8, 8)) Condition(child, depth + 1);
        }
    }

    private static string Label(PackageCatalog catalog, uint tag) =>
        catalog.GetName(tag) is { Length: > 0 } name ? $"{name} (0x{tag:X8})" : $"0x{tag:X8}";

    private static string Perks(int count) => count == 1 ? "1 perk" : $"{count} perks";

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));

    /** Row offsets of one typed array; a malformed array yields none. */
    private static IEnumerable<int> Rows(byte[] bytes, int field, int stride)
    {
        if (field + 16 > bytes.Length) yield break;
        long count = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field));
        long header = field + 8 + BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(field + 8));
        if (count is <= 0 or > 4096 || header < 4 || header + 16 + count * stride > bytes.Length) yield break;
        for (int index = 0; index < count; index++) yield return (int)(header + 16 + index * stride);
    }

    /** Node offsets of one pointer array; a malformed array yields none. */
    private static IEnumerable<int> Nodes(byte[] bytes, int field, int stride)
    {
        foreach (int at in Rows(bytes, field, stride))
        {
            long node = at + BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
            if (node >= 4 && node < bytes.Length) yield return (int)node;
        }
    }
}
