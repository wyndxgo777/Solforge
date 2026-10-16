using System.Runtime.CompilerServices;
using SunriseStudio.Packages;

namespace SunriseStudio.Scene;

/** Art entities of armor and weapon items, read from the item's art block at +136. */
public static class GuardianArt
{
    // Hash of the empty string; a slot holding it names no art.
    internal const uint EmptyHash = 0x811C9DC5;
    private const uint ItemArtClass = 0x808077B5, HolderClass = 0x80805DFE, ArtHashClass = 0x80805E01;
    // Item definition +136 points to the art block; art block +88 is the u16 gear row.
    private const int ArtBlockField = 136, GearRowField = 88;
    private const ushort NoGearRow = 0xFFFF;
    // Gear row +16 is the key that picks the weapon's row in each presentation config.
    private const int GearKeyField = 16;
    private const uint ComponentRowClass = 0x80809C04, ComponentClass = 0x80809C36;
    // Weapon resource: a typed row array at +1416, 232-byte rows, a default row at +1184; row +0x6C is the tag.
    private const uint PresentationClass = 0x80804221, PresentationRowClass = 0x80803E72;
    private const int PresentationRows = 1416, PresentationDefault = 1184, PresentationRowStride = 232, ThirdPersonField = 0x6C;
    private static readonly ConditionalWeakTable<PackageCatalog, GuardianArtTables> SharedTables = new();

    /** Art entities of an armor or weapon item for one class byte and gender. Empty when the item has no art block. */
    public static IReadOnlyList<uint> ItemEntities(PackageCatalog catalog, byte[] itemDefinition, byte classByte, bool female) =>
        ItemEntities(TablesFor(catalog), itemDefinition, classByte, female);

    /** The weapon's gameplay root from gear row +88; 0 when the item has none (+88 == 0xFFFF). */
    public static uint GameplayEntity(PackageCatalog catalog, byte[] itemDefinition) =>
        GameplayEntity(TablesFor(catalog), itemDefinition);

    /**
     * The weapon's third-person animation tag: +0x6C of its presentation row keyed by gear row +16, else of the
     * default row. The empty hash when the item has no gear row or no presentation component.
     */
    public static uint ThirdPersonTag(PackageCatalog catalog, byte[] itemDefinition)
    {
        var tables = TablesFor(catalog);
        if (GearRow(tables, itemDefinition) is not { } row) return EmptyHash;
        uint key = tables.Gear.Data.U32(row + GearKeyField);
        var entity = new TagData(catalog.Read(GameplayEntity(tables, itemDefinition)));
        foreach (int component in entity.Rows(0x10, ComponentRowClass, 12))
        {
            uint tag = entity.U32(component);
            if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != ComponentClass) continue;
            var config = new TagData(catalog.Read(tag));
            int resource = config.Relative(0x18);
            if (!config.Has(resource - 4, 4) || config.U32(resource - 4) != PresentationClass) continue;
            int selected = config.Rows(resource + PresentationRows, PresentationRowClass, PresentationRowStride)
                .FirstOrDefault(at => config.U32(at) == key, resource + PresentationDefault);
            return config.U32(selected + ThirdPersonField);
        }
        return EmptyHash;
    }

    // The public wrappers share one table set per catalog.
    private static GuardianArtTables TablesFor(PackageCatalog catalog) => SharedTables.GetValue(catalog, value => new(value));

    /** Resolves the gameplay entity named by a gear item definition; 0 when it has no gear row. */
    internal static uint GameplayEntity(GuardianArtTables tables, byte[] itemDefinition)
    {
        if (GearRow(tables, itemDefinition) is not { } row) return 0;
        uint name = tables.Gear.Data.U32(row + 4);
        if (!tables.RuntimeNames.TryGetValue(name, out uint entity))
            throw new InvalidDataException($"Gear name 0x{name:X8} does not resolve.");
        tables.RequireClass(entity, GuardianArtTables.EntityClass);
        return entity;
    }

    // Offset of the item's gear row in the gear table; null when the item has none.
    private static int? GearRow(GuardianArtTables tables, byte[] itemDefinition)
    {
        var item = new TagData(itemDefinition);
        int art = ArtBlock(item);
        if (art < 0) return null;
        ushort gearRow = item.U16(art + GearRowField);
        if (gearRow == NoGearRow) return null;
        var rows = tables.Gear.Rows;
        if (gearRow >= rows.Length) throw new InvalidDataException($"Gear row {gearRow} is outside its table.");
        return rows[gearRow];
    }

    /**
     * The first art row whose class byte is -1 or the class byte. A female takes arrangement +12 when it resolves
     * to a model, else +8. Every item also takes hash 0 of each arrangement group; only weapons have groups today.
     */
    internal static IReadOnlyList<uint> ItemEntities(GuardianArtTables tables, byte[] itemDefinition, byte classByte, bool female)
    {
        var item = new TagData(itemDefinition);
        int art = ArtBlock(item);
        if (art < 0) return [];
        int arrangement = -1;
        foreach (int artRow in item.Rows(art, ItemArtClass, 4))
        {
            sbyte rowClass = (sbyte)item.U8(artRow);
            if (rowClass != -1 && rowClass != classByte) continue;
            arrangement = item.U16(artRow + 2);
            break;
        }
        if (arrangement < 0) throw new InvalidDataException($"No art row matches class {classByte}.");
        int row = tables.Arrangement(arrangement);
        var table = tables.Arrangements;
        var result = new List<uint>();
        uint male = table.U32(row + 8), femaleHash = table.U32(row + 12);
        if (female && !IsEmpty(femaleHash) && tables.TryEntity(femaleHash, out uint femaleEntity)) result.Add(femaleEntity);
        else if (!IsEmpty(male)) result.Add(tables.Entity(male));
        foreach (var (_, hashes) in Groups(table, row))
            if (hashes.Length != 0 && !IsEmpty(hashes[0])) result.Add(tables.Entity(hashes[0]));
        return result.Distinct().ToArray();
    }

    /** Selector and art hashes of each group of one arrangement row, in row order; a selector may repeat. */
    internal static IReadOnlyList<(int Selector, uint[] Hashes)> Groups(TagData table, int row)
    {
        var result = new List<(int, uint[])>();
        foreach (int holder in table.Rows(row + 16, HolderClass, 8))
        {
            if (table.I64(holder) == 0) throw new InvalidDataException("An art group pointer is empty.");
            int group = table.Relative(holder);
            result.Add((table.U8(group), table.Rows(group + 8, ArtHashClass, 4).Select(table.U32).ToArray()));
        }
        return result;
    }

    /** True for the zero hash and the empty-art hash. */
    internal static bool IsEmpty(uint hash) => hash is 0 or EmptyHash;

    // -1 when the item has no art block.
    private static int ArtBlock(TagData item) => item.I64(ArtBlockField) == 0 ? -1 : item.Relative(ArtBlockField);
}

/** The arrangement, gear and map tables of one catalog; thread-safe. */
internal sealed class GuardianArtTables
{
    internal const uint WrapperClass = 0x8080744A, EntityClass = 0x80809C0F;
    private const uint ArtMapRowClass = 0x808056EC, RuntimeMapRowClass = 0x80809252;
    private const uint ArrangementRowClass = 0x80805DFB, GearRowClass = 0x80805B7C;
    private readonly PackageCatalog catalog;
    private readonly int[] arrangementRows;
    private readonly Lazy<Dictionary<uint, uint>> artMap, runtimeMap;
    private readonly Lazy<(TagData Data, int[] Rows)> gear;

    public GuardianArtTables(PackageCatalog catalog)
    {
        this.catalog = catalog;
        Arrangements = PackageHashMap.ReadStructured(catalog, GuardianAppearance.ArrangementTable);
        arrangementRows = Arrangements.Rows(8, ArrangementRowClass, 32);
        artMap = new(() => PackageHashMap.Read(catalog, GuardianAppearance.ArtMap, ArtMapRowClass));
        runtimeMap = new(() => PackageHashMap.Read(catalog, GuardianAppearance.RuntimeMap, RuntimeMapRowClass));
        gear = new(() =>
        {
            var data = PackageHashMap.ReadStructured(catalog, GuardianAppearance.GearTable);
            return (data, data.Rows(8, GearRowClass, 48));
        });
    }

    public TagData Arrangements { get; }
    public (TagData Data, int[] Rows) Gear => gear.Value;
    /** Runtime map 0x80EC3F60: gear name hash -> entity. */
    public IReadOnlyDictionary<uint, uint> RuntimeNames => runtimeMap.Value;

    /** Offset of arrangement row index in Arrangements. */
    public int Arrangement(int index) => (uint)index < (uint)arrangementRows.Length ? arrangementRows[index]
        : throw new InvalidDataException($"Arrangement {index} is outside its table.");

    /** Art hash -> map 0x80EC3F61 -> 0x8080744A wrapper; wrapper +16 is the entity. */
    public uint Entity(uint hash) => TryEntity(hash, out uint entity) ? entity
        : throw new InvalidDataException($"Art hash 0x{hash:X8} does not resolve to an entity.");

    /** False when the map misses the hash, the wrapper is not 0x8080744A, or wrapper +16 is not an entity. */
    public bool TryEntity(uint hash, out uint entity)
    {
        entity = 0;
        if (!artMap.Value.TryGetValue(hash, out uint wrapper) || !IsClass(wrapper, WrapperClass)) return false;
        uint candidate = new TagData(catalog.Read(wrapper)).U32(16);
        if (!IsClass(candidate, EntityClass)) return false;
        entity = candidate;
        return true;
    }

    /** Throws unless the tag is of the given class. */
    public void RequireClass(uint tag, uint type)
    {
        if (!IsClass(tag, type)) throw new InvalidDataException($"Tag 0x{tag:X8} is not class 0x{type:X8}.");
    }

    private bool IsClass(uint tag, uint type) => catalog.TryGetEntry(tag, out var entry) && entry.Reference == type;
}
