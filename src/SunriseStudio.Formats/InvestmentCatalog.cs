using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Formats;

/** Catalog of items, vendors, stats and perks read from the investment index tags, used by the item and vendor editors. Broad item category. */
public enum InvestmentCategory { Weapons, Armor, Items, Currencies }
/** Tag hashes of the index tables the catalog reads (items, displays, string banks, vendors, icons and so on). */
public sealed record InvestmentSources(uint Items = InvestmentTags.ItemIndex, uint Displays = InvestmentTags.ItemDisplayIndex,
    uint StringBanks = 0x81A27211, uint StatNames = 0x81613D07, uint Vendors = InvestmentTags.VendorIndex,
    uint VendorDisplays = InvestmentTags.VendorDisplayIndex, uint PlugSets = 0x81319349, uint Icons = 0x81A291C2,
    uint Perks = 0x81613D28, uint StatGroups = 0x81613D08, uint Collectibles = 0x81327CDE,
    uint CollectibleDisplays = 0x81327D67, uint PresentationNodes = 0x8131933F, uint PresentationNodeDisplays = 0x81613CF8,
    uint ItemArt = InvestmentItemArt.TableTag);
/** One item definition from the item index, with its resolved display name and stack/tier data. */
public sealed record InvestmentItem(int Index, uint Hash, uint Tag, uint DisplayTag, string Name,
    string TypeName, string Description, byte Bucket, byte Tier, int MaximumStack, bool Instanced)
{
    public InvestmentCategory Category => Bucket <= 2 ? InvestmentCategory.Weapons : Bucket is >= 3 and <= 7
        ? InvestmentCategory.Armor : InvestmentItemKinds.IsCurrency(Bucket, TypeName)
            ? InvestmentCategory.Currencies : InvestmentCategory.Items;
    public string Rarity => InvestmentItemKinds.TierName(Tier);
}
/** One vendor from the vendor index; LabelSource says where its name came from. */
public sealed record InvestmentVendor(int Index, uint Hash, uint Tag, uint DisplayTag, string Name,
    string LabelSource = "Package name or index");
/** A stat row of an item: the stat, its value and the editable field; Conditional/Computed mark values that depend on context. */
public sealed record InvestmentStat(byte Index, string Name, int Value, AssetProperty Field,
    bool Conditional = false, bool Computed = false);
/** A stat known to the installation, by index and display name. */
public sealed record InvestmentStatDefinition(byte Index, string Name);
/** A socket of an item: type, initial plug and the plugs it allows. */
public sealed record InvestmentSocket(int Index, ushort Type, ushort InitialPlug, string PlugName,
    AssetProperty TypeField, AssetProperty PlugField, IReadOnlyList<ushort> AllowedPlugs);
/** A perk row of an item with its editable field. */
public sealed record InvestmentPerk(ushort Index, string Name, AssetProperty Field, bool Conditional = false);
/** A sandbox perk definition: hash, text and icon index. */
public sealed record InvestmentPerkDefinition(int Index, uint Hash, string Name, string Description, ushort IconIndex);
/** Everything shown for one item: stats, sockets, perks, raw fields and diagnostics. */
public sealed record InvestmentItemDetail(InvestmentItem Item, IReadOnlyList<InvestmentStat> Stats,
    IReadOnlyList<InvestmentSocket> Sockets, IReadOnlyList<InvestmentPerk> Perks,
    IReadOnlyList<AssetProperty> Fields, IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyDictionary<byte, InvestmentStatCurve> DisplayCurves { get; init; } = new Dictionary<byte, InvestmentStatCurve>();
}
/** One cost of a vendor sale: item, quantity and their editable fields. */
public sealed record InvestmentCost(ushort ItemIndex, string Name, uint Quantity, AssetProperty ItemField,
    AssetProperty QuantityField, bool Conditional);
/** One vendor sale (item and category) with its costs. */
public sealed record InvestmentSale(int Index, ushort ItemIndex, string Name, int Category,
    AssetProperty ItemField, AssetProperty CategoryField, IReadOnlyList<InvestmentCost> Costs)
{
    public uint OwnerTag { get; init; }
    public string SourceSha256 { get; init; } = "";
}
/** Everything shown for one vendor: sales, reset interval/phase and diagnostics. */
public sealed record InvestmentVendorDetail(InvestmentVendor Vendor, IReadOnlyList<InvestmentSale> Sales,
    uint ResetIntervalRaw, uint ResetPhaseRaw, IReadOnlyList<string> Diagnostics);

/** Item identities join only when the definition and display tables agree on their hash. */
public sealed partial class InvestmentCatalog
{
    public const uint ItemIndexTag = InvestmentTags.ItemIndex, DisplayIndexTag = InvestmentTags.ItemDisplayIndex;
    public const uint VendorIndexTag = InvestmentTags.VendorIndex, VendorDisplayIndexTag = InvestmentTags.VendorDisplayIndex;
    public const uint ItemClass = InvestmentTags.ItemClass, DisplayClass = InvestmentTags.ItemDisplayClass,
        VendorClass = InvestmentTags.VendorClass;
    private readonly PackageCatalog catalog;
    private readonly LocalizedStrings strings;
    private readonly uint[] banks;
    private readonly Dictionary<uint, InvestmentItem> byTag;
    private readonly Dictionary<uint, InvestmentVendor> vendorsByTag;
    private readonly Dictionary<byte, string> statNames;
    private readonly InvestmentSources sources;
    public IReadOnlyList<InvestmentItem> Items { get; }
    public IReadOnlyList<InvestmentVendor> Vendors { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public IReadOnlyList<InvestmentStatDefinition> StatDefinitions { get; }
    public IReadOnlyList<InvestmentPerkDefinition> PerkDefinitions { get; }
    public int PerkCount => PerkDefinitions.Count;

    private InvestmentCatalog(PackageCatalog catalog, LocalizedStrings strings, uint[] banks,
        InvestmentItem[] items, InvestmentVendor[] vendors, Dictionary<byte, string> statNames, string[] diagnostics,
        InvestmentSources sources, InvestmentPerkDefinition[] perks)
    {
        this.catalog = catalog; this.strings = strings; this.banks = banks;
        this.sources = sources;
        Items = items; Vendors = vendors; this.statNames = statNames; Diagnostics = diagnostics;
        StatDefinitions = statNames.Select(pair => new InvestmentStatDefinition(pair.Key, pair.Value))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Index).ToArray();
        PerkDefinitions = perks;
        byTag = new(); vendorsByTag = new();
        foreach (var item in items) { byTag.TryAdd(item.Tag, item); if (item.DisplayTag != 0) byTag.TryAdd(item.DisplayTag, item); }
        foreach (var vendor in vendors) { vendorsByTag.TryAdd(vendor.Tag, vendor); if (vendor.DisplayTag != 0) vendorsByTag.TryAdd(vendor.DisplayTag, vendor); }
    }

    /** Item for a tag, or null. */
    public InvestmentItem? FindItem(uint tag) => byTag.GetValueOrDefault(tag);
    /** Vendor for a tag, or null. */
    public InvestmentVendor? FindVendor(uint tag) => vendorsByTag.GetValueOrDefault(tag);
    /** Item name for an index; "None" for the empty index and a placeholder when out of range. */
    public string ItemName(int index) => index == ushort.MaxValue ? "None" : index >= 0 && index < Items.Count
        ? Items[index].Name : $"Item {index}";
    /** Stat name for an index, or a placeholder. */
    public string StatName(byte index) => statNames.GetValueOrDefault(index, $"Stat {index}");
    /** Resolves a {bank index, string hash} pair through the bank index. */
    public string Text(uint bank, uint hash) => bank < banks.Length ? strings.Get(banks[bank], hash)?.Trim() ?? "" : "";
    /** Collections pages that list collectibles, read from the current package bytes. */
    public IReadOnlyList<InvestmentCollectionNode> CollectionNodes() => InvestmentCollections.ReadNodes(
        catalog.Read(sources.PresentationNodes), catalog.Read(sources.PresentationNodeDisplays), Text);
    /** Unnamed perks keep their index so two of them never share a label. */
    public string PerkName(int index) => index >= 0 && index < PerkDefinitions.Count && PerkDefinitions[index].Name.Length != 0
        ? PerkDefinitions[index].Name : $"Sandbox perk {index}";

    /** Package reads run on the caller's worker and never write extracted content to the project. */
    public static InvestmentCatalog Load(PackageCatalog catalog, LocalizedStrings? strings = null,
        CancellationToken token = default, Action<string>? progress = null, InvestmentSources? sources = null)
    {
        token.ThrowIfCancellationRequested();
        sources ??= new();
        var diagnostics = new List<string>();
        uint[] banks = [];
        if (catalog.ContainsTag(sources.StringBanks))
        {
            var data = new TagReader(catalog.Read(sources.StringBanks));
            var array = data.Array(8, stride: 8);
            banks = Enumerable.Range(0, array.Count).Select(index => data.U32(array.Data + index * 8 + 4)).ToArray();
        }
        strings ??= LocalizedStrings.LoadBanks(catalog, banks, token);
        diagnostics.AddRange(strings.Diagnostics);
        string Text(TagReader data, int offset)
        {
            if (!data.Has(offset, 8)) return "";
            uint bank = data.U32(offset), hash = data.U32(offset + 4);
            return bank < banks.Length ? strings.Get(banks[bank], hash)?.Trim() ?? "" : "";
        }
        var definitions = ReadIndex(catalog, sources.Items);
        var displays = ReadIndex(catalog, sources.Displays);
        var items = new InvestmentItem[definitions.Length];
        for (int i = 0; i < definitions.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var definition = definitions[i];
            uint displayTag = i < displays.Length && displays[i].Hash == definition.Hash ? displays[i].Tag : 0;
            string name = "", type = "", description = ""; byte bucket = 255, tier = 255; int maximum = 0; bool instanced = false;
            try
            {
                RequireClass(catalog, definition.Tag, ItemClass);
                var data = new TagReader(catalog.Read(definition.Tag));
                if (!data.Has(MaximumOffset, 8)) throw new InvalidDataException("Item fixed fields are truncated.");
                maximum = data.I32(MaximumOffset); bucket = data.U8(BucketOffset); tier = data.U8(TierOffset); instanced = data.U8(InstancedOffset) != 0;
                if (displayTag != 0)
                {
                    RequireClass(catalog, displayTag, DisplayClass);
                    var display = new TagReader(catalog.Read(displayTag));
                    name = Text(display, ItemNameOffset); type = Text(display, ItemTypeOffset); description = Text(display, ItemDescriptionOffset);
                }
            }
            catch (Exception error) when (Recoverable(error)) { diagnostics.Add($"Item {i}: {error.Message}"); }
            if (name.Length == 0) name = catalog.GetName(definition.Tag) ?? $"Item {i}";
            items[i] = new(i, definition.Hash, definition.Tag, displayTag, name, type, description, bucket, tier, maximum, instanced);
            if ((i & 255) == 0) progress?.Invoke($"Reading items {i + 1}/{definitions.Length}");
        }
        var stats = new Dictionary<byte, string>();
        if (catalog.ContainsTag(sources.StatNames))
            try
            {
                var data = new TagReader(catalog.Read(sources.StatNames)); var array = data.Array(8, stride: 36);
                for (int i = 0; i < array.Count && i < 256; i++)
                { string name = Text(data, array.Data + i * 36 + 4); stats[(byte)i] = name.Length != 0 ? name : $"Stat {i}"; }
            }
            catch (Exception error) when (Recoverable(error)) { diagnostics.Add("Stat names: " + error.Message); }
        var vendorRows = ReadIndex(catalog, sources.Vendors, optional: true);
        var vendorDisplays = ReadIndex(catalog, sources.VendorDisplays, optional: true);
        var vendors = new InvestmentVendor[vendorRows.Length];
        for (int i = 0; i < vendors.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            uint display = i < vendorDisplays.Length && vendorDisplays[i].Hash == vendorRows[i].Hash ? vendorDisplays[i].Tag : 0;
            string name = catalog.GetName(vendorRows[i].Tag) ?? $"Vendor {i}";
            string labelSource = "Package name or index";
            if (display != 0)
                try
                {
                    RequireClass(catalog, display, VendorDisplayClass);
                    var data = new TagReader(catalog.Read(display));
                    if (!data.Has(VendorNameOffset, 8)) throw new InvalidDataException("Vendor display name is truncated.");
                    ushort bank = data.U16(VendorNameOffset);
                    string text = bank <= short.MaxValue && bank < banks.Length
                        ? strings.Get(banks[bank], data.U32(VendorNameOffset + 4))?.Trim() ?? "" : "";
                    if (text.Length != 0)
                    { name = text.Split('\n')[0]; labelSource = "Authored vendor display name"; }
                }
                catch (Exception error) when (Recoverable(error)) { diagnostics.Add($"Vendor label {i}: {error.Message}"); }
            vendors[i] = new(i, vendorRows[i].Hash, vendorRows[i].Tag, display, name, labelSource);
        }
        InvestmentPerkDefinition[] perks = [];
        if (catalog.ContainsTag(sources.Perks))
            try
            {
                RequireClass(catalog, sources.Perks, SandboxPerkDisplays.TableClass);
                string Pair(uint bank, uint hash) => bank < banks.Length ? strings.Get(banks[bank], hash)?.Trim() ?? "" : "";
                perks = SandboxPerkDisplays.Read(catalog, sources.Perks).Select(row => new InvestmentPerkDefinition(
                    row.Index, row.Hash, Pair(row.NameBank, row.NameHash), Pair(row.DescriptionBank, row.DescriptionHash), row.IconIndex)).ToArray();
                if (perks.Length > ushort.MaxValue) throw new InvalidDataException("Sandbox perk index exceeds its native range.");
            }
            catch (Exception error) when (Recoverable(error)) { perks = []; diagnostics.Add("Sandbox perks: " + error.Message); }
        return new(catalog, strings, banks, items, vendors, stats, diagnostics.ToArray(), sources, perks);
    }

    /** The row index is the native identity; missing rows never shift later indices. */
    private static (uint Hash, uint Tag)[] ReadIndex(PackageCatalog catalog, uint tag, bool optional = false)
    {
        if (optional && !catalog.ContainsTag(tag)) return [];
        var data = new TagReader(catalog.Read(tag)); var array = data.Array(8, stride: IndexRowStride);
        if (array.Count > ushort.MaxValue) throw new InvalidDataException("Investment index exceeds its native range.");
        return Enumerable.Range(0, array.Count).Select(index =>
        { int p = array.Data + index * IndexRowStride; return (data.U32(p + IndexHashOffset), data.U32(p + IndexTagOffset)); }).ToArray();
    }

    private static void RequireClass(PackageCatalog catalog, uint tag, uint expected)
    {
        if (!catalog.TryGetEntry(tag, out var entry) || entry.Type is not (8 or 16) || entry.Reference != expected)
            throw new InvalidDataException("Investment reference has an unexpected class.");
    }
    private static bool Recoverable(Exception error) => error is InvalidDataException or NotSupportedException
        or ArgumentException or IndexOutOfRangeException or OverflowException or KeyNotFoundException;
}
