namespace SunriseStudio.Formats;

/** Label filter compilation: converts authored label lists into the native mask form. */
public static partial class NativePerkBehavior
{
    // Compiled filters: kind 0 holds any-of and none-of masks, kind 1 all four; 0xFF means no filter.
    private const uint KindZeroClass = 0x808093F6, KindOneClass = 0x808093F5;
    private const int KindZeroSize = 84, KindOneSize = 164;

    /** A label set at Set (four row arrays, name +64, tag +72) and its filter kind at Kind (compiled pointer +8). */
    private sealed record LabelSite(string Key, string Label, int Set, int Kind, string Description = "");

    /** Every class with a label set and compiled filter; checked against every installed program. */
    private static LabelSite[] LabelSitesOf(uint type) => type switch
    {
        DamageScaleClass => [new("source", "Source labels", 72, 160), new("damage", "Damage labels", 192, 272)],
        UnequipClass or 0x80803DF5 or 0x80803DFA or 0x80803DF7 or 0x80803DDE or 0x80803DDD or 0x80803DE2 =>
            [new("item", "Item labels", 16, 96)],
        0x808029E0 => [new("weapon", "Weapon labels", 16, 96)],
        KillClass => [new("victim", "Victim labels", 8, 88), new("source", "Damage source labels", 208, 296)],
        DamageClass or 0x80803E1A or 0x80803DDC => [new("source", "Damage source labels", 40, 128)],
        0x80803E3F or 0x80803E3E or 0x808029EC => [new("slot", "Weapon slot labels", 8, 88)],
        ResistClass => [new("source", "Damage source labels", 48, 136)],
        0x8080281C => [new("item", "Item labels", 8, 88)],
        0x80803DE5 => [new("first", "Labels (payload +40)", 8, 88), new("second", "Labels (payload +0)", 176, 256)],
        0x80803DF8 => [new("message", "Message labels", 8, 88)],
        0x80803DFC => [new("victim", "Victim labels", 8, 88), new("killer", "Killer labels", 104, 184),
            new("source", "Damage source labels", 232, 320)],
        _ => []
    };

    /** Byte ranges a label site owns, left out of the "other settings" comparisons. */
    private static IEnumerable<(int Offset, int Length)> LabelRanges(uint type) =>
        LabelSitesOf(type).SelectMany(site => new[] { (site.Set, 80), (site.Kind, 1), (site.Kind + 8, 8) });

    private static List<PerkLabelFilter> EmptyFilters(uint type) =>
        [.. LabelSitesOf(type).Select(site => new PerkLabelFilter { Key = site.Key, Label = site.Label, Description = site.Description })];

    /** The label hashes of each of a site's four arrays. */
    private static List<uint>[] ReadLabelArrays(TagReader data, int at, LabelSite site) =>
    [
        .. Enumerable.Range(0, 4).Select(a =>
        {
            var rows = data.Array(at + site.Set + a * 16, LabelRowClass, 24);
            return Enumerable.Range(0, rows.Count).Select(i => data.U32(rows.Data + i * 24)).ToList();
        })
    ];

    private static void ReadLabelFilters(ReadOnlyMemory<byte> bytes, int at, PerkBehaviorNode node)
    {
        var data = new TagReader(bytes);
        foreach (var site in LabelSitesOf(node.NativeClass))
        {
            var arrays = ReadLabelArrays(data, at, site);
            byte kind = data.U8(at + site.Kind);
            var filter = new PerkLabelFilter
            {
                Key = site.Key, Label = site.Label, Description = site.Description,
                AnyOf = arrays[0], AllOf = arrays[1], NoneOf = arrays[2], NotAllOf = arrays[3],
                // Only the forms the compiler writes are editable: rows with a compiled filter, or neither.
                Editable = kind is 0 or 1 && data.I64(at + site.Kind + 8) != 0 && arrays.Any(array => array.Count != 0)
                    || kind == byte.MaxValue && data.I64(at + site.Kind + 8) == 0 && arrays.All(array => array.Count == 0)
            };
            node.LabelFilters.Add(filter);
        }
    }

    /**
     * Compiles four label arrays into the native filter: kind 0 when all-of and not-all-of are empty, else kind 1.
     * @return Kind 0xFF and no body when every array is empty; null when a hash is not in the table.
     */
    public static (byte Kind, byte[] Body)? CompileLabelFilter(PerkLabelTable table, IReadOnlyList<IReadOnlyList<uint>> arrays)
    {
        if (arrays.All(array => array.Count == 0)) return (byte.MaxValue, []);
        var masks = new byte[4][];
        for (int a = 0; a < 4; a++)
        {
            masks[a] = new byte[PerkLabelTable.MaskBytes];
            foreach (uint hash in arrays[a])
                if (!table.AddTo(hash, masks[a])) return null;
        }
        if (arrays[1].Count == 0 && arrays[3].Count == 0)
        {
            byte[] zero = new byte[KindZeroSize];
            masks[0].CopyTo(zero, 0); masks[2].CopyTo(zero, 40);
            zero[80] = arrays[0].Count == 0 ? (byte)1 : (byte)0;
            zero[81] = arrays[2].Count == 0 ? (byte)1 : (byte)0;
            return (0, zero);
        }
        byte[] one = new byte[KindOneSize];
        for (int a = 0; a < 4; a++)
        {
            masks[a].CopyTo(one, a * 40);
            one[160 + a] = arrays[a].Count == 0 ? (byte)1 : (byte)0;
        }
        return (1, one);
    }

    /** Rows, compiled filter, kind byte and pointer; a new node also points its label set at the label table. */
    private static void WriteLabelFilter(PerkWriter writer, int at, LabelSite site, PerkLabelFilter filter, bool fresh)
    {
        var arrays = filter.Arrays();
        // An empty filter needs no table: it compiles to the no-filter form.
        var table = arrays.All(array => array.Count == 0) ? new PerkLabelTable([], [])
            : PerkLabelTable.Installed ?? throw new InvalidDataException("Label filters need the installed label table.");
        var compiled = CompileLabelFilter(table, arrays) ?? throw new InvalidDataException($"{filter.Label} has an unknown label.");
        int path = writer.String(LabelGlobalsPath);
        if (fresh)
        {
            writer.I64(at + site.Set + 64, path - at - site.Set - 64);
            writer.U32(at + site.Set + 72, LabelGlobalsTag);
        }
        for (int a = 0; a < 4; a++)
        {
            int rows = writer.Array(at + site.Set + a * 16, LabelRowClass, arrays[a].Count, 24);
            for (int i = 0; i < arrays[a].Count; i++)
            {
                int row = rows + i * 24;
                writer.U32(row, arrays[a][i]);
                writer.I64(row + 8, path - row - 8);
                writer.U32(row + 16, LabelGlobalsTag);
            }
        }
        writer.U8(at + site.Kind, compiled.Kind);
        if (compiled.Kind == byte.MaxValue) { writer.I64(at + site.Kind + 8, 0); return; }
        int body = writer.Node(compiled.Kind == 0 ? KindZeroClass : KindOneClass, compiled.Body.Length);
        writer.Copy(body, compiled.Body);
        writer.I64(at + site.Kind + 8, body - at - site.Kind - 8);
    }

    /** Fresh nodes write every filter; installed nodes rewrite only a filter whose arrays changed. */
    private static void WriteLabelFilters(PerkWriter writer, ReadOnlyMemory<byte>? original, PerkBehaviorNode node, int at)
    {
        var sites = LabelSitesOf(node.NativeClass);
        foreach (var filter in node.LabelFilters)
        {
            var site = sites.First(row => row.Key == filter.Key);
            if (original is { } bytes && SameArrays(ReadLabelArrays(new TagReader(bytes), at, site), filter.Arrays())) continue;
            WriteLabelFilter(writer, at, site, filter, original is null);
        }
    }

    private static bool SameArrays(List<uint>[] left, List<uint>[] right) =>
        left.Zip(right).All(pair => pair.First.SequenceEqual(pair.Second));

    /** Filter problems: keys must match, read-only filters must be unchanged, and every label must be known. */
    private static IEnumerable<string> FilterProblems(PerkBehaviorNode node, PerkBehaviorNode? original)
    {
        var expected = original?.LabelFilters ?? EmptyFilters(node.NativeClass);
        if (!expected.Select(filter => filter.Key).SequenceEqual(node.LabelFilters.Select(filter => filter.Key)))
        {
            yield return $"{node.Title} has missing or extra label filters.";
            yield break;
        }
        foreach (var (filter, before) in node.LabelFilters.Zip(expected))
        {
            if (SameArrays(filter.Arrays(), before.Arrays())) continue;
            if (!before.Editable) { yield return $"{node.Title}: {filter.Label} is read-only."; continue; }
            if (PerkLabelTable.Installed is not { } table) { yield return $"{node.Title}: label filters need the installed label table."; continue; }
            foreach (uint hash in filter.Arrays().SelectMany(array => array).Where(hash => !table.Contains(hash)).Distinct())
                yield return $"{node.Title}: {filter.Label} has unknown label 0x{hash:X8}.";
        }
    }
}
