using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

/**
 * Hands out package ids for new project packages inside the range the client accepts,
 * PackageWriter.FirstNewPackageId..LastNewPackageId (0x0100-0x0CFF) (Solforge Phase 18). Ids that an
 * installed package or one of the project's added assets already uses are never handed out; the
 * project manifest keeps each asset's id, so allocations survive a reload without extra state.
 */
public sealed class PackageIdAllocator
{
    public const ushort First = PackageWriter.FirstNewPackageId, Last = PackageWriter.LastNewPackageId;
    public const int Capacity = Last - First + 1;

    private readonly bool[] used = new bool[Capacity];
    private int count;

    public PackageIdAllocator(IEnumerable<ushort>? usedIds = null)
    {
        foreach (ushort id in usedIds ?? []) if (PackageWriter.IsNewPackageId(id)) Mark(id);
    }

    /** Ids taken by the installation (and its overlay) plus every native added asset of the project. */
    public static PackageIdAllocator For(PackageCatalog catalog, StudioProject? project = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var allocator = new PackageIdAllocator();
        for (int id = First; id <= Last; id++) if (catalog.ContainsInstalledPackage((ushort)id)) allocator.Mark((ushort)id);
        foreach (var asset in project?.AddedAssets ?? [])
            if (asset.Native is { } native && PackageWriter.IsNewPackageId(native.PackageId)) allocator.Mark(native.PackageId);
        return allocator;
    }

    public int Available => Capacity - count;

    /** True when the id is taken, or is not in the range of new ids. */
    public bool IsUsed(ushort id) => !PackageWriter.IsNewPackageId(id) || used[id - First];

    /** The lowest free id; throws when every id of the range is taken. */
    public ushort Allocate()
    {
        for (int index = 0; index < Capacity; index++)
            if (!used[index])
            {
                ushort id = (ushort)(First + index);
                Mark(id);
                return id;
            }
        throw new InvalidOperationException($"Every new package id ({PackageWriter.NewPackageIdRange}) is in use.");
    }

    /** Claims a specific id; false when it is outside 0x0100-0x0CFF or already used. */
    public bool TryReserve(ushort id)
    {
        if (IsUsed(id)) return false;
        Mark(id);
        return true;
    }

    /** Returns an id to the pool, e.g. after the asset that needed it was removed. */
    public void Release(ushort id)
    {
        if (!PackageWriter.IsNewPackageId(id) || !used[id - First]) return;
        used[id - First] = false;
        count--;
    }

    private void Mark(ushort id)
    {
        if (used[id - First]) return;
        used[id - First] = true;
        count++;
    }
}
