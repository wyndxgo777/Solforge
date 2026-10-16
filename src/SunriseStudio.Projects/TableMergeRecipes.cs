namespace SunriseStudio.Projects;

/** How a row field holds a pointer; the values are the merge sidecar's field kinds. */
internal enum MergeFieldKind : byte { OwnedArray = 1, RowPointer = 2, ObjectPointer = 3 }

/** How the merge rebuilds an array; the values are the merge sidecar's op kinds. */
internal enum MergeOpKind : byte { Append = 1, SortedMap = 2 }

/** A row field that holds a self-relative pointer. */
internal sealed record MergeField(int Offset, MergeFieldKind Kind)
{
    /** Layout of an owned array's elements; null when they hold no pointers. */
    public MergeShape? Element { get; init; }
    /** Element size of an owned array, or 0 when it is not known. */
    public int ElementStride { get; init; }
    /** Descriptor offset of the array a row pointer points into. */
    public int TargetArray { get; init; }
    /** A stock row whose array here changed gets a nested op. */
    public bool Nestable { get; init; }
}

/** Every pointer a row of this size holds. */
internal sealed record MergeShape(int Stride, IReadOnlyList<MergeField> Fields)
{
    /** Pointers from an element to an object it owns; both must sit in the mod's added bytes. */
    public IReadOnlyList<MergeLocalPointer> Local { get; init; } = [];
}

/** A self-relative i64 at Offset to an object laid out as Target. */
internal sealed record MergeLocalPointer(int Offset, MergeShape Target);

/** One typed array the merge rebuilds; Descriptor is its {i64 count, i64 rel} offset in the entry. */
internal sealed record MergeArray(string Name, int Descriptor, MergeOpKind Kind, MergeShape Row)
{
    public string? PairGroup { get; init; }
    public uint Limit { get; init; } = TableMergeRecipes.EditorRowLimit;
    /** Offset of the u32 key in a row, or -1 for none. */
    public int KeyOffset { get; init; } = -1;
    /** Keys must be unique across the merge; set only when the stock keys are. */
    public bool Unique { get; init; }
    /** Keys must stay strictly ascending; set only when the stock keys are. */
    public bool OrderedWhenStockAscending { get; init; }
}

/** Every array of one installed table that the merge rebuilds. */
internal sealed record MergeRecipe(uint Tag, string Name, IReadOnlyList<MergeArray> Arrays);

/**
 * How a merge step moves through data: follow a pointer, visit array rows, visit a pool range, or require a
 * byte value.
 */
internal enum MergeStepKind { Pointer, Array, Pool, Equals }

/**
 * One step from the entry root toward a site field.
 * Pointer follows the i64 at Offset; Array visits each row of the array whose descriptor is at Offset;
 * Pool visits rows [first, first + count) of the root array at Offset; Equals keeps a node whose byte matches.
 */
internal sealed record MergeStep(MergeStepKind Kind, int Offset)
{
    public int Stride { get; init; }
    /** Class word just before a pointer's target; 0 skips the check. */
    public uint Marker { get; init; }
    public int FirstOffset { get; init; }
    public int CountOffset { get; init; }
    public byte Value { get; init; }
}

/** A field whose value is a row number in a target array; OpLocal targets an array of the same entry. */
internal sealed record MergeSiteRule(string Name, IReadOnlyList<MergeStep> Path, int Field, int Width, bool Signed,
    uint TargetTable, int TargetArray)
{
    public bool OpLocal { get; init; }
}

/** The tables whose rows the runtime merges, and the fields that name their rows. */
internal static class TableMergeRecipes
{
    /** Studio's typed-array writers stop at this many rows. */
    public const uint EditorRowLimit = 1_048_576;
    /** Native readers sign-extend a 16-bit row number. */
    public const uint SignedRowLimit = 0x8000;
    /** Studio stops these tables at short.MaxValue rows. */
    public const uint ShortRowLimit = 0x7FFF;
    /** An item display picks its stat group with a signed byte. */
    public const uint StatGroupLimit = 128;

    public const uint ItemIndex = 0x81327CCB, ItemDisplayIndex = 0x81613CF1, IconIndex = 0x81A291C2, ItemArt = 0x81A28B23;
    public const uint Collectibles = 0x81327CDE, CollectibleDisplays = 0x81327D67, PresentationNodes = 0x8131933F;
    public const uint PerkDefinitions = 0x81319327, PerkDisplays = 0x81613D28, SocketTypes = 0x8131932F;
    public const uint SocketTypeDisplays = 0x81613D06, LoreText = 0x81613CF2, LoreRows = 0x81319348;
    public const uint StatGroups = 0x81613D08, Gear = 0x81613D27, GearDefinitions = 0x81319326;
    public const uint Arrangements = 0x81613D23, ArrangementDefinitions = 0x81319329;
    public const uint Vendors = 0x8131931D, VendorDisplays = 0x81A2926D, StringBankIndex = 0x81A27211;
    public const uint RuntimeNames = 0x80EC3F60, ArtNames = 0x80EC3F61, HudIcons = 0x80EFC07B, PlugSets = 0x81319349;
    public const uint ItemClass = 0x80807BEA, ItemDisplayClass = 0x80805CE1, VendorClass = 0x80807850;
    public const uint VendorDisplayClass = 0x808058EE, NodeDisplays = 0x81613CF8;

    private const int Rows = 8;

    private static MergeShape Plain(int stride) => new(stride, []);
    private static MergeField Owned(int offset, int elementStride = 0, MergeShape? element = null) =>
        new(offset, MergeFieldKind.OwnedArray) { ElementStride = elementStride, Element = element };
    private static MergeArray Append(string name, int descriptor, MergeShape row, string? pair, uint limit) =>
        new(name, descriptor, MergeOpKind.Append, row) { PairGroup = pair, Limit = limit };
    private static MergeArray Sorted(string name, int stride) =>
        new(name, Rows, MergeOpKind.SortedMap, Plain(stride)) { KeyOffset = 0 };

    // Index rows: hash at +0, tag at +16.
    private static readonly MergeShape IndexRow = Plain(24);
    // A stat row's curve is a typed array of 8-byte points at +8.
    private static readonly MergeShape StatRow = new(24, [Owned(8, 8)]);
    // An arrangement holder points at a 24-byte group whose art hashes are a typed array at +8.
    private static readonly MergeShape Holder = new(8, []) { Local = [new(0, new(24, [Owned(8, 4)]))] };

    public static readonly IReadOnlyDictionary<uint, MergeRecipe> Tables = new MergeRecipe[]
    {
        new(ItemIndex, "item index", [Append("rows", Rows, IndexRow, "item", SignedRowLimit) with { KeyOffset = 0, Unique = true }]),
        new(ItemDisplayIndex, "item display index",
            [Append("rows", Rows, IndexRow, "item", SignedRowLimit) with { KeyOffset = 0, Unique = true }]),
        new(IconIndex, "icon index", [Append("rows", Rows, IndexRow, null, SignedRowLimit)]),
        new(ItemArt, "item art",
        [
            Append("handles", 8, Plain(4), null, EditorRowLimit), Append("integers", 24, Plain(4), null, EditorRowLimit),
            Append("fields", 40, Plain(8), null, EditorRowLimit), Append("items", 56, Plain(16), "item", SignedRowLimit)
        ]),
        new(Collectibles, "collectibles",
        [
            Append("rows", Rows, new(184, [Owned(24, 2), Owned(48), Owned(64), Owned(80), Owned(96), Owned(112),
                Owned(136), Owned(168)]), "collectible", SignedRowLimit) with { KeyOffset = 40, Unique = true }
        ]),
        new(CollectibleDisplays, "collectible displays", [Append("rows", Rows, new(96, [Owned(72)]), "collectible", SignedRowLimit)]),
        new(PresentationNodes, "presentation nodes",
        [
            Append("rows", Rows, new(168, [Owned(24), Owned(48), Owned(64), Owned(88), Owned(104, 24),
                Owned(120, 4) with { Nestable = true }, Owned(136, 4), Owned(152, 4)]), null, SignedRowLimit)
        ]),
        new(PerkDefinitions, "sandbox perk definitions", [Append("rows", Rows, Plain(8), "perk", SignedRowLimit)]),
        new(PerkDisplays, "sandbox perk displays",
        [
            Append("rows", Rows, new(24, [new(8, MergeFieldKind.RowPointer) { TargetArray = 24 }]), "perk", SignedRowLimit),
            Append("text", 24, Plain(28), null, EditorRowLimit)
        ]),
        new(SocketTypes, "socket types",
            [Append("rows", Rows, new(96, [Owned(16), Owned(40, 8), Owned(56), Owned(80)]), "socket_type", ShortRowLimit)]),
        new(SocketTypeDisplays, "socket type displays", [Append("rows", Rows, Plain(40), "socket_type", ShortRowLimit)]),
        new(LoreText, "lore text", [Append("rows", Rows, Plain(40), "lore", ShortRowLimit)]),
        new(LoreRows, "lore rows", [Append("rows", Rows, Plain(16), "lore", ShortRowLimit)]),
        new(StatGroups, "stat groups",
            [Append("rows", Rows, new(56, [Owned(16, 24, StatRow), Owned(32)]), null, StatGroupLimit)]),
        // Gear row +8 points at an object, not an array.
        new(Gear, "gear", [Append("rows", Rows, new(48, [new(8, MergeFieldKind.ObjectPointer), Owned(32)]),
            "gear", SignedRowLimit)]),
        new(GearDefinitions, "gear definitions", [Append("rows", Rows, Plain(8), "gear", SignedRowLimit)]),
        // The +24 group pool stays in place: stock holders point into it.
        new(Arrangements, "arrangements",
            [Append("rows", Rows, new(32, [Owned(16, 8, Holder)]), "arrangement", SignedRowLimit)]),
        new(ArrangementDefinitions, "arrangement definitions", [Append("rows", Rows, Plain(4), "arrangement", SignedRowLimit)]),
        new(Vendors, "vendor index", [Append("rows", Rows, IndexRow, "vendor", ShortRowLimit)]),
        new(VendorDisplays, "vendor display index", [Append("rows", Rows, IndexRow, "vendor", ShortRowLimit)]),
        new(StringBankIndex, "string bank index",
        [
            Append("rows", Rows, Plain(8), null, SignedRowLimit) with
            { KeyOffset = 0, Unique = true, OrderedWhenStockAscending = true }
        ]),
        // The runtime name map's +24 array is not rebuilt; Studio never changes it.
        new(RuntimeNames, "runtime name map", [Sorted("rows", 8)]),
        new(ArtNames, "art name map", [Sorted("rows", 8)]),
        new(HudIcons, "HUD icon library", [Sorted("rows", 112)])
    }.ToDictionary(recipe => recipe.Tag);

    private static MergeStep Pointer(int offset, uint marker = 0) => new(MergeStepKind.Pointer, offset) { Marker = marker };
    private static MergeStep Each(int offset, int stride) => new(MergeStepKind.Array, offset) { Stride = stride };
    private static MergeStep Is(int offset, byte value) => new(MergeStepKind.Equals, offset) { Value = value };
    private static MergeSiteRule Rule(string name, MergeStep[] path, int field, int width, uint table, int array = Rows,
        bool signed = false) => new(name, path, field, width, signed, table, array);

    private static readonly MergeStep[] SocketRows = [Pointer(104, 0x808077C1), Each(0, 80)];
    private static readonly MergeStep[] Root = [];

    /** Site rules for single-record entries, by entry class. */
    public static readonly IReadOnlyDictionary<uint, IReadOnlyList<MergeSiteRule>> ByClass =
        new Dictionary<uint, IReadOnlyList<MergeSiteRule>>
        {
            [ItemClass] =
            [
                Rule("lore row", [Pointer(40, 0x808077ED)], 0, 2, LoreText),
                Rule("socket type", SocketRows, 0, 2, SocketTypes),
                Rule("initial plug", SocketRows, 2, 2, ItemIndex),
                Rule("socket plug", [.. SocketRows, Each(64, 32)], 0, 4, ItemIndex),
                Rule("sandbox perk", [Pointer(112, 0x808077B9), Each(16, 24)], 0, 2, PerkDefinitions),
                Rule("gear row", [Pointer(136)], 88, 2, Gear),
                Rule("arrangement row", [Pointer(136), Each(0, 4)], 2, 2, Arrangements)
            ],
            [ItemDisplayClass] =
            [
                Rule("icon row", Root, 128, 2, IconIndex),
                Rule("name bank", Root, 132, 4, StringBankIndex),
                Rule("type bank", Root, 144, 4, StringBankIndex),
                Rule("flavor bank", Root, 152, 4, StringBankIndex),
                Rule("stat group", [Pointer(112, 0x80805CF1)], 20, 1, StatGroups, signed: true)
            ],
            [VendorClass] =
            [
                Rule("sale item", [Each(48, 184)], 70, 2, ItemIndex),
                Rule("cost item", [Each(48, 184), Each(32, 48)], 0, 2, ItemIndex)
            ],
            [VendorDisplayClass] =
            [
                Rule("name bank", Root, 64, 2, StringBankIndex),
                Rule("subtitle bank", Root, 72, 2, StringBankIndex)
            ]
        };

    /** Site rules for the rows of installed tables, by table tag. */
    public static readonly IReadOnlyDictionary<uint, IReadOnlyList<MergeSiteRule>> ByTable =
        new Dictionary<uint, IReadOnlyList<MergeSiteRule>>
        {
            [Collectibles] = [Rule("item row", [Each(Rows, 184)], 44, 2, ItemIndex)],
            [CollectibleDisplays] =
            [
                Rule("icon row", [Each(Rows, 96)], 4, 4, IconIndex),
                Rule("name bank", [Each(Rows, 96)], 8, 4, StringBankIndex),
                Rule("flavor bank", [Each(Rows, 96)], 16, 4, StringBankIndex),
                Rule("source bank", [Each(Rows, 96)], 24, 4, StringBankIndex)
            ],
            [PresentationNodes] = [Rule("collectible row", [Each(Rows, 168), Each(120, 4)], 0, 2, Collectibles)],
            [NodeDisplays] = [Rule("name bank", [Each(Rows, 44)], 8, 4, StringBankIndex)],
            [PerkDisplays] =
            [
                Rule("icon row", [Each(24, 28)], 16, 2, IconIndex),
                Rule("name bank", [Each(24, 28)], 0, 4, StringBankIndex),
                Rule("description bank", [Each(24, 28)], 8, 4, StringBankIndex)
            ],
            [LoreText] =
            [
                Rule("title bank", [Each(Rows, 40)], 12, 2, StringBankIndex),
                Rule("subtitle bank", [Each(Rows, 40)], 20, 2, StringBankIndex),
                Rule("body bank", [Each(Rows, 40)], 28, 2, StringBankIndex)
            ],
            [PlugSets] = [Rule("plug item", [Each(Rows, 24), Each(8, 32)], 0, 4, ItemIndex)],
            [ItemArt] =
            [
                Rule("first field", [Each(56, 16)], 8, 4, ItemArt, 40, signed: true) with { OpLocal = true },
                Rule("first handle", [Each(40, 8), Is(4, 1)], 0, 4, ItemArt, 8, signed: true) with { OpLocal = true },
                Rule("first integer", [Each(40, 8), Is(4, 5)], 0, 4, ItemArt, 24, signed: true) with { OpLocal = true },
                Rule("sandbox perk", [Each(40, 8), Is(4, 5),
                    new(MergeStepKind.Pool, 24) { Stride = 4, FirstOffset = 0, CountOffset = 5 }], 0, 4, PerkDefinitions)
            ]
        };

    /** FNV-1 over the lowercase ASCII name: offset basis 0x811C9DC5, prime 0x01000193, multiply then xor. */
    public static uint PairGroupId(string name)
    {
        uint hash = 0x811C9DC5;
        foreach (char character in name.ToLowerInvariant())
        {
            if (character > 0x7F) throw new ArgumentException("Pair group names are ASCII.", nameof(name));
            hash = unchecked(hash * 0x01000193) ^ character;
        }
        return hash;
    }
}
