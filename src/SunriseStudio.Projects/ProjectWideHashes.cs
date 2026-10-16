/** File: Partial StudioProject: validation and editing of wide-hash aliases on added assets. */
using SunriseStudio.Packages;

namespace SunriseStudio.Projects;

/** Partial: wide-hash alias operations. */
public sealed partial class StudioProject
{
    /** Metadata-only edits keep immutable payload paths and derived deltas unchanged. */
    public bool SetWideHashAliases(string id, IEnumerable<ulong> aliases, PackageCatalog catalog)
    {
        RequireMutationAllowed();
        ArgumentNullException.ThrowIfNull(catalog);
        var values = new WideHashAliases(aliases);
        var asset = AddedAssets.FirstOrDefault(value => value.Id == id)
            ?? throw new KeyNotFoundException("Added asset is not in the project.");
        var native = asset.Native ?? throw new InvalidOperationException("Wide hashes need a native package asset.");
        if ((native.WideHashes ?? WideHashAliases.Empty).Equals(values)) return false;
        var updated = asset with { Native = native with { WideHashes = values.Count == 0 ? null : values } };
        var assets = AddedAssets.Select(value => value.Id == id ? updated : value).ToArray();
        RequireWideHashes(assets, catalog);
        ReplaceManifest(manifest with { AddedAssets = assets });
        return true;
    }

    /** Installed claims remain reserved even when their current target is ambiguous or incompatible. */
    public void ValidateWideHashAliases(PackageCatalog catalog) => RequireWideHashes(AddedAssets, catalog);

    /** The full candidate state is checked before publication, including undo and coupled additions. */
    private static void RequireWideHashes(IReadOnlyList<AddedProjectAsset> assets, PackageCatalog? catalog,
        bool requireCatalog = true)
    {
        var keys = new HashSet<ulong>();
        var counts = new Dictionary<ushort, int>();
        foreach (var asset in assets)
        {
            if (asset.Native is not { WideHashes.Count: > 0 } native) continue;
            if (!PackageWriter.IsNewPackageId(native.PackageId))
                throw new InvalidDataException($"Authored wide hashes need a package id from {PackageWriter.NewPackageIdRange}.");
            if (catalog is null && requireCatalog)
                throw new InvalidOperationException("Wide-hash authoring needs the original installation for collision checks.");
            if (catalog?.ContainsInstalledPackage(native.PackageId) == true)
                throw new InvalidDataException($"Package id {native.PackageId:X4} is already installed.");
            int count = checked(counts.GetValueOrDefault(native.PackageId) + native.WideHashes.Count);
            if (count > WideHashAliases.MaximumCount)
                throw new InvalidDataException("A package has too many wide-hash aliases.");
            counts[native.PackageId] = count;
            foreach (ulong key in native.WideHashes)
            {
                if (!keys.Add(key)) throw new InvalidDataException($"Wide hash {key:X16} names more than one project asset.");
                if (catalog?.HasInstalledWideHashClaim(key) == true)
                    throw new InvalidDataException($"Wide hash {key:X16} is already claimed by installed content.");
            }
        }
    }
}
