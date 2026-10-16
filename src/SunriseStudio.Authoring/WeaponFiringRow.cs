using System.Buffers.Binary;
using SunriseStudio.Formats;
using SunriseStudio.Packages;
using SunriseStudio.Projects;

namespace SunriseStudio.Authoring;

/**
 * The HUD config row that the weapon's gear key selects, and the installed row it was copied from.
 * Row and BaseRow are byte offsets into Hud and BaseHud.
 */
public sealed record WeaponFiringRow(uint Root, uint Hud, int Row, uint BaseHud, int BaseRow)
{
    // Firing row slots: +0x60 restarts on every shot, +0x90 is never paused, +0xF0 is the projectile.
    public const int MuzzleSlot = 0x60, ReloadSlot = 0x90, ProjectileSlot = 0xF0;
    private const uint HudResourceClass = 0x80803AC9, HudRowClass = 0x80803ACF;
    private const int HudRows = 0x240, HudStride = 448, HudKey = 16;

    /**
     * Finds the weapon's own firing row; the gameplay root and the HUD config must be owned.
     * @param read Reads the root and its configs with the caller's pending changes.
     */
    public static WeaponFiringRow Find(StudioProject project, PackageCatalog catalog, Func<uint, byte[]> read, uint weaponTag)
    {
        var owned = project.AddedAssets.Where(asset => asset.Native is not null).ToDictionary(asset => NativeTag(asset.Native!));
        var art = WeaponArchetypeAuthoring.Read(catalog, catalog.Read(weaponTag));
        uint root = art.EntityTag;
        if (!owned.ContainsKey(root)) throw new InvalidDataException("The weapon's gameplay root must be authored in this project.");

        uint key = WeaponArchetypeAuthoring.GearKey(catalog, art);
        uint hud = 0; int row = -1;
        foreach (uint config in WeaponConfigEdits.ConstructionConfigs(read(root)).Distinct())
            if (FiringRow(read(config), key) is int found and >= 0) { hud = config; row = found; break; }
        if (row < 0) throw new InvalidDataException("No config holds a firing row for the weapon's gear key.");
        if (!owned.ContainsKey(hud)) throw new InvalidDataException("Give the weapon its own base records first; its firing row is shared.");

        // The stock slot entities come from the same row of the config this firing row was cloned from.
        if (owned[hud].BaseTagHash is not uint baseHud || RowOffsets(catalog.ReadOriginal(baseHud)) is not { } baseRows
            || RowOffsets(read(hud)) is not { } rows || baseRows.Count != rows.Count || !rows.Contains(row))
            throw new InvalidDataException("The weapon's firing row is not a copy of an installed firing row.");
        return new(root, hud, row, baseHud, baseRows[rows.IndexOf(row)]);
    }

    /** The raw tag at this slot of the installed base row; 0 when empty. Not checked to be an entity. */
    public uint StockTag(PackageCatalog catalog, int slot) => U32(catalog.ReadOriginal(BaseHud), BaseRow + slot);

    /** Tag of a native asset from its package id and entry index. */
    public static uint NativeTag(NativeAssetMetadata native) =>
        checked(0x80800000u + ((uint)native.PackageId << 13) + (uint)native.EntryIndex!.Value);

    /** The offset of the HUD row with the key, or -1 when the config is not a HUD config or has none. */
    private static int FiringRow(byte[] bytes, uint key) =>
        RowOffsets(bytes)?.FirstOrDefault(at => U32(bytes, at + HudKey) == key, -1) ?? -1;

    /** The offsets of a HUD config's firing rows, or null when the config is not a HUD config. */
    private static List<int>? RowOffsets(byte[] bytes)
    {
        if (bytes.Length < 32) return null;
        long resource = 24 + I64(bytes, 24);
        if (resource < 4 || resource > bytes.Length - HudRows - 16 || U32(bytes, (int)resource - 4) != HudResourceClass) return null;
        int field = (int)resource + HudRows;
        long count = I64(bytes, field), header = field + 8 + I64(bytes, field + 8);
        if (count is <= 0 or > 1024 || header < 4 || header > bytes.Length - 16 || U32(bytes, (int)header + 8) != HudRowClass) return null;
        return Enumerable.Range(0, (int)count).Select(index => (int)header + 16 + index * HudStride)
            .Where(at => at + HudStride <= bytes.Length).ToList();
    }

    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static long I64(byte[] bytes, int at) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));
}
