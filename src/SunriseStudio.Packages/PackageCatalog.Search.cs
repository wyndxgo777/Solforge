namespace SunriseStudio.Packages;

// Solforge Phase 05: lookups by class hash, entry type and package group over the immutable entry table.
// Tag-hash lookup is upstream TryGetEntry; these indexes build once on first use and never change.
public sealed partial class PackageCatalog
{
    private Dictionary<uint, PackageEntry[]>? byClass;
    private Dictionary<ulong, ushort[]>? byGroup;

    /** Entries whose class hash (the entry's Reference) matches, in catalog order. */
    public IReadOnlyList<PackageEntry> FindByClass(uint classHash)
    {
        var index = byClass ??= Entries.GroupBy(x => x.Reference).ToDictionary(x => x.Key, x => x.ToArray());
        return index.TryGetValue(classHash, out var found) ? found : [];
    }

    /** Entries of one type (TypeInfo bits 9-15) and, unless subType is negative, one subtype (bits 6-8). */
    public IReadOnlyList<PackageEntry> FindByType(int type, int subType = -1) =>
        Entries.Where(x => x.Type == type && (subType < 0 || x.SubType == subType)).ToArray();

    /** The header group of an installed package id, or null when the id is not installed. */
    public ulong? GetPackageGroup(ushort packageId) =>
        packages.TryGetValue(packageId, out var package) ? package.Group : null;

    /** Installed package ids whose newest patch carries the group, ascending. */
    public IReadOnlyList<ushort> FindPackagesInGroup(ulong group)
    {
        var index = byGroup ??= packages.Values.GroupBy(x => x.Group)
            .ToDictionary(x => x.Key, x => x.Select(p => p.Id).Order().ToArray());
        return index.TryGetValue(group, out var found) ? found : [];
    }

    /** The entries of one installed package in entry-index order, or none when it is not installed. */
    public IReadOnlyList<PackageEntry> GetPackageEntries(ushort packageId) =>
        packages.TryGetValue(packageId, out var package) ? package.Entries : [];
}
