namespace SunriseStudio.Packages;

/** Snapshot half of PackageCatalog: copies of the overlay metadata. */
public sealed partial class PackageCatalog
{
    /** Returns one metadata snapshot; arbitrary read overrides may change tags absent from it. */
    public IReadOnlyList<PackageOverlayEntry> GetOverlayEntries() =>
        Volatile.Read(ref overlayState).Entries.Values.ToArray();

    /** File identity includes change time, so restoring the write timestamp does not retain an index. */
    public string GetPackageFileIdentity(CancellationToken token = default) =>
        CacheFileIdentity.Compute(Directory.EnumerateFiles(PackageDirectory, "*.pkg"), token);
}
