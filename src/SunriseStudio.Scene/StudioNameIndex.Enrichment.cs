using System.Collections.Concurrent;
using System.Text;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/** Enrichment part of StudioNameIndex: item and perk names and item facts. */
public sealed partial class StudioNameIndex
{
    private static readonly UTF8Encoding StrictText = new(false, true);

    /** Only declared references produce owner labels; aligned integer matches are never owners. */
    private partial void LoadEnrichment(CancellationToken token, Action<string>? progress)
    {
        Stage("Investment names", progress);
        ReadInvestmentNames(token);
        Publish("Investment names", false);
        Stage("CUI and behavior names", progress);
        ReadContentNames(token, progress);
        Publish("CUI and behavior names", false);
        Stage("Sunrise SDK names", progress);
        ReadSdkNames(token, progress);
        Publish("Sunrise SDK names", false);
    }

    // Investment index, table and class tags the name pass reads.
    private const uint StringBankIndexTag = 0x81A27211, StringBankIndexClass = 0x80805F98;
    private const uint ItemIndexTag = InvestmentTags.ItemIndex, ItemDisplayIndexTag = InvestmentTags.ItemDisplayIndex;
    private const uint ItemClass = InvestmentTags.ItemClass, ItemDisplayClass = InvestmentTags.ItemDisplayClass;
    private const uint VendorIndexTag = InvestmentTags.VendorIndex, VendorDisplayIndexTag = InvestmentTags.VendorDisplayIndex;
    private const uint VendorClass = InvestmentTags.VendorClass, VendorDisplayClass = InvestmentTags.VendorDisplayClass;
    private const uint PerkProgramMapTag = 0x80EC3F60, PerkProgramMapClass = 0x80809780, PerkProgramRowClass = 0x80809252;
    private const uint PerkProgramClass = InvestmentTags.PerkProgramClass;
    // Unset bucket and tier; an item whose definition cannot be read keeps these.
    private const byte UnreadByte = 255;

    /** Facts of every 0x80807BEA in the item index, keyed by definition tag. */
    private readonly ConcurrentDictionary<uint, InvestmentItemFacts> itemFacts = [];
    // Replaced whole, never changed in place.
    private uint[] investmentBanks = [];

    /** Looks up the facts of an item definition tag. */
    public bool TryGetItem(uint definitionTag, out InvestmentItemFacts facts) => itemFacts.TryGetValue(definitionTag, out facts);

    /** Bank indices resolve within their named string container, never through a global hash guess. */
    private void ReadInvestmentNames(CancellationToken token)
    {
        TryRead("Investment string banks", () => Volatile.Write(ref investmentBanks, ReadBanks()));
        Dictionary<uint, (uint Hash, uint Tag)> display = [];
        TryRead("Item display index", () => display = ReadIndex(ItemDisplayIndexTag));
        Dictionary<uint, string> typeLines = [];
        foreach (var (index, row) in display)
        {
            token.ThrowIfCancellationRequested();
            TryRead($"Item display {row.Tag:X8}", () =>
            {
                if (!catalog.TryGetEntry(row.Tag, out var entry) || entry.Reference != ItemDisplayClass) return;
                var data = new TagData(catalog.Read(row.Tag));
                if (InvestmentText(data, 144) is { Length: > 0 } line) typeLines[index] = line;
                string? name = InvestmentText(data, 132);
                if (string.IsNullOrWhiteSpace(name)) return;
                itemNames[index] = name;
                AddTag(row.Tag, new(name, StudioNameSource.Localization, StudioNameConfidence.Authored,
                    "Item display name", 10, row.Tag, 132));
                AddHash(row.Hash, new(name, StudioNameSource.Localization, StudioNameConfidence.Authored,
                    "Investment definition identity", 10, row.Tag, 132));
            });
        }
        TryRead("Item definition index", () =>
        {
            Dictionary<uint, HashSet<string>> perkOwners = [];
            foreach (var (index, row) in ReadIndex(ItemIndexTag))
            {
                token.ThrowIfCancellationRequested();
                uint target = row.Tag, identity = row.Hash;
                if (index > ushort.MaxValue || !catalog.TryGetEntry(target, out var entry) || entry.Reference != ItemClass) continue;
                uint displayTag = display.TryGetValue(index, out var displayRow) && displayRow.Hash == identity ? displayRow.Tag : 0;
                string line = displayTag != 0 ? typeLines.GetValueOrDefault(index, "") : "";
                // Unnamed items still get facts; only a joined display names the definition.
                string? name = displayTag != 0 ? itemNames.GetValueOrDefault(index) : null;
                if (name is not null)
                    AddTag(target, new(name, StudioNameSource.Localization, StudioNameConfidence.Authored,
                        "Item definition and display index join", 10, displayTag, 132));
                var facts = UnreadItem((ushort)index, identity, displayTag, line);
                if (TryData($"Item {target:X8}", target) is { } item)
                {
                    TryRead($"Item facts {target:X8}", () => facts = ItemFacts(item, (ushort)index, identity, displayTag, line));
                    if (name is not null) TryRead($"Item perks {target:X8}", () => AddPerkOwners(item, name, perkOwners));
                }
                itemFacts[target] = facts;
            }
            foreach (var (perk, owners) in perkOwners)
                perkNames[perk] = owners.Count == 1 ? $"{owners.First()} perk" : $"Shared perk ({owners.Count} items)";
        });
        TryRead("Vendor names", ReadVendorNames);
        Dictionary<uint, uint> programs = [];
        TryRead("Perk program map", () => programs = ReadPerkPrograms());
        TryRead("Sandbox perk names", () =>
        {
            Dictionary<uint, HashSet<string>> programNames = [];
            foreach (var perk in SandboxPerkDisplays.Read(catalog))
            {
                if (BankText(perk.NameBank, perk.NameHash) is not { Length: > 0 } name) continue;
                perkNames[(uint)perk.Index] = name;
                if (!programs.TryGetValue(perk.GameplayHash, out uint program)) continue;
                if (!programNames.TryGetValue(program, out var names)) programNames[program] = names = [];
                names.Add(name);
            }
            // A program shared by differently named perks keeps its earlier label.
            foreach (var (program, names) in programNames)
                if (names.Count == 1 && catalog.TryGetEntry(program, out var entry) && entry.Reference == PerkProgramClass)
                    AddTag(program, new(names.First(), StudioNameSource.Localization, StudioNameConfidence.Authored,
                        "Sandbox perk display and runtime name map", 10, SandboxPerkDisplays.Tag));
        });
    }

    /** Perk rows of the stats block at +112; each u16 is a sandbox perk index. */
    private static void AddPerkOwners(TagData item, string name, Dictionary<uint, HashSet<string>> perkOwners)
    {
        if (!item.Has(112, 8) || item.I64(112) == 0) return;
        int block = item.Relative(112);
        foreach (int p in item.Rows(block + 16, 0x808077BC, 24))
        {
            uint perk = item.U16(p);
            if (perk == ushort.MaxValue) continue;
            if (!perkOwners.TryGetValue(perk, out var owners)) perkOwners[perk] = owners = [];
            owners.Add(name);
        }
    }

    /** Bucket +184, tier +186; the objective +48, plug +64 and sack +88 blocks are set when non-zero. */
    private static InvestmentItemFacts ItemFacts(TagData item, ushort index, uint hash, uint displayTag, string line)
    {
        if (!item.Has(180, 8)) throw new InvalidDataException("Item fixed fields are truncated.");
        return new(index, hash, displayTag, item.U8(184), item.U8(186), line,
            item.I64(48) != 0, item.I64(64) != 0, item.I64(88) != 0);
    }

    private static InvestmentItemFacts UnreadItem(ushort index, uint hash, uint displayTag, string line) =>
        new(index, hash, displayTag, UnreadByte, UnreadByte, line, false, false, false);

    /**
     * Rereads one item's facts from its definition and display.
     * @param displayTag Joined display tag, or zero when the index rows disagree.
     */
    private InvestmentItemFacts ReadItemFacts(uint definition, ushort index, uint hash, uint displayTag)
    {
        string line = "";
        if (displayTag != 0 && catalog.TryGetEntry(displayTag, out var shown) && shown.Reference == ItemDisplayClass)
            TryRead($"Item display {displayTag:X8}", () => line = InvestmentText(new TagData(catalog.Read(displayTag)), 144) ?? "");
        var facts = UnreadItem(index, hash, displayTag, line);
        TryRead($"Item facts {definition:X8}", () => facts = ItemFacts(new TagData(catalog.Read(definition)), index, hash, displayTag, line));
        return facts;
    }

    /** Vendor display names: a u16 bank at +64 and its hash at +68; the first line is the name. */
    private void ReadVendorNames()
    {
        var displays = ReadIndex(VendorDisplayIndexTag);
        foreach (var (index, row) in ReadIndex(VendorIndexTag))
        {
            if (!displays.TryGetValue(index, out var display) || display.Hash != row.Hash
                || !catalog.TryGetEntry(row.Tag, out var vendor) || vendor.Reference != VendorClass
                || !catalog.TryGetEntry(display.Tag, out var shown) || shown.Reference != VendorDisplayClass) continue;
            TryRead($"Vendor display {display.Tag:X8}", () =>
            {
                var data = new TagData(catalog.Read(display.Tag));
                if (!data.Has(64, 8) || data.U16(64) > short.MaxValue) return;
                if (BankText(data.U16(64), data.U32(68)) is not { Length: > 0 } text) return;
                AddTag(row.Tag, new(text.Split('\n')[0], StudioNameSource.Localization, StudioNameConfidence.Authored,
                    "Vendor display name", 10, display.Tag, 64));
            });
        }
    }

    /** Gameplay name hash to perk program tag; an absent or foreign map gives none. */
    private Dictionary<uint, uint> ReadPerkPrograms()
    {
        Dictionary<uint, uint> programs = [];
        if (!catalog.TryGetEntry(PerkProgramMapTag, out var entry) || entry.Reference != PerkProgramMapClass) return programs;
        var data = new TagData(catalog.Read(PerkProgramMapTag));
        foreach (int p in data.Rows(8, PerkProgramRowClass, 8))
            if (data.U32(p) is not (0 or 0x811C9DC5)) programs[data.U32(p)] = data.U32(p + 4);
        return programs;
    }

    private uint[] ReadBanks()
    {
        if (!catalog.TryGetEntry(StringBankIndexTag, out var entry) || entry.Reference != StringBankIndexClass) return [];
        var data = new TagData(catalog.Read(StringBankIndexTag));
        return SafeRows(data, 8, 8).Select(p => data.U32(p + 4)).ToArray();
    }

    /** Investment index rows by row number: identity hash at +0, target tag at +16. */
    private Dictionary<uint, (uint Hash, uint Tag)> ReadIndex(uint tag)
    {
        if (!catalog.TryGetEntry(tag, out _)) return [];
        var data = new TagData(catalog.Read(tag));
        var rows = SafeRows(data, 8, 24);
        var result = new Dictionary<uint, (uint Hash, uint Tag)>(rows.Length);
        for (int i = 0; i < rows.Length; i++) result[(uint)i] = (data.U32(rows[i]), data.U32(rows[i] + 16));
        return result;
    }

    private TagData? TryData(string label, uint tag)
    {
        TagData? data = null;
        TryRead(label, () => data = new TagData(catalog.Read(tag)));
        return data;
    }

    private string? BankText(uint bank, uint hash)
    {
        uint[] banks = Volatile.Read(ref investmentBanks);
        return bank < banks.Length && GameNames is { } game ? game.Strings.Get(banks[bank], hash)?.Trim() : null;
    }

    private string? InvestmentText(TagData data, int offset) =>
        data.Has(offset, 8) ? BankText(data.U32(offset), data.U32(offset + 4)) : null;

    /** Embedded source paths name their containing resource, not every object that links to it. */
    private void ReadContentNames(CancellationToken token, Action<string>? progress)
    {
        var selected = catalog.Entries.Where(x => x.Type is 8 or 16
            && x.Reference is 0x80804825 or 0x8080496A or 0x8080941E or 0x80800000).ToArray();
        for (int i = 0; i < selected.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = selected[i];
            TryRead($"Content names {entry.TagHash:X8}", () =>
            {
                var data = new TagData(catalog.Read(entry.TagHash));
                var names = InlineStrings(data).ToArray();
                foreach (var (text, offset) in names)
                    AddHash(Fnv1(text), new(text, StudioNameSource.PackageSource, StudioNameConfidence.Authored,
                        "Package inline string", 25, entry.TagHash, offset));
                string[] paths = names.Select(x => x.Text).Where(IsContentPath).Distinct(StringComparer.Ordinal).ToArray();
                if (paths.Length == 1)
                    AddTag(entry.TagHash, new(SourceLeaf(paths[0]), StudioNameSource.PackageSource,
                        StudioNameConfidence.Contextual, paths[0], 45, entry.TagHash));
                if (entry.Reference == 0x8080496A && data.Has(28, 4))
                {
                    uint child = data.U32(28);
                    if (catalog.TryGetEntry(child, out var target) && target.Reference == 0x80804825)
                        AddContext(child, Describe(entry), "CUI resource", entry.TagHash);
                }
            });
            if ((i & 255) == 0) Stage("CUI and behavior names", progress, i + 1, selected.Length);
        }
        Stage("CUI and behavior names", progress, selected.Length, selected.Length);
    }

    /** Only bounded UTF-8 inline strings are package-backed preimages; each carries its own offset. */
    private static IEnumerable<(string Text, int Offset)> InlineStrings(TagData data) =>
        InlineStringBlocks.Strings(data).Select(item => (item.Text, item.Start)).ToList();

    private static bool IsContentPath(string text) => text.Contains('/') || text.Contains('\\')
        ? text.EndsWith(".tft", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".cui", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".pattern", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) : false;
    private static string SourceLeaf(string path)
    {
        string leaf = path.Replace('\\', '/').Split('/')[^1];
        return leaf.EndsWith(".pattern.tft", StringComparison.OrdinalIgnoreCase) ? leaf[..^12]
            : Path.GetFileNameWithoutExtension(leaf);
    }

    private static int[] SafeRows(TagData data, int field, int stride)
    {
        if (!data.Has(field, 16)) throw new InvalidDataException("Missing name table descriptor.");
        if (data.I64(field) == 0) return [];
        int header = data.Relative(field + 8);
        if (!data.Has(header, 16)) throw new InvalidDataException("Name table header leaves its asset.");
        return data.Rows(field, data.U32(header + 8), stride);
    }

    private void TryRead(string label, Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException
            or OverflowException or KeyNotFoundException or IOException)
        {
            if (error.Message.StartsWith("Package block authentication failed", StringComparison.Ordinal)) throw;
            diagnostics.Enqueue($"{label}: {error.Message}");
        }
    }
}
