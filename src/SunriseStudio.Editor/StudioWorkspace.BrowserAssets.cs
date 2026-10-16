using SunriseStudio.Packages;
using SunriseStudio.Scene;

namespace SunriseStudio.Editor;

/** StudioWorkspace part: builds the asset browser rows, package list and the indexes over them. */
public sealed partial class StudioWorkspace
{
    private int installedBrowserCount;
    private string[] installedBrowserPackages = ["All packages"];
    private (PackageEntry Entry, string Name)[] addedBrowserEntries = [];

    /** Browser rows and every index over them, built together on the worker. */
    private sealed record BrowserRowSet(BrowserAsset[] Assets, string[] Packages, Dictionary<string, int> KindCounts,
        Dictionary<uint, int> ComponentRows, Dictionary<string, BrowserAsset[]> CategoryRows,
        Dictionary<uint, string> CategoryOf, Dictionary<uint, uint> DisplayOwner, Dictionary<PickClass, BrowserAsset[]> ClassRows);

    /** Installed rows keep their order; native project rows occupy the trailing range. */
    private void ResetBrowserRows(BrowserRowSet rows)
    {
        Assets = rows.Assets; BrowserPackages = rows.Packages;
        kindCounts = rows.KindCounts; componentRows = rows.ComponentRows;
        CategoryRows = rows.CategoryRows;
        CategoryCounts = rows.CategoryRows.ToDictionary(pair => pair.Key, pair => pair.Value.Length, StringComparer.Ordinal);
        categoryOf = rows.CategoryOf;
        displayOwner = rows.DisplayOwner;
        classRows = rows.ClassRows;
        installedClassCounts = rows.ClassRows.ToDictionary(pair => pair.Key, pair => pair.Value.Length);
        PublishBrowserKinds();
        installedBrowserCount = rows.Assets.Length;
        installedBrowserPackages = rows.Packages;
        addedBrowserEntries = [];
    }

    /** Builds every row once; category rows come first, in registry and rank order, then the rest in catalog order. */
    private BrowserRowSet CreateRows(PackageCatalog catalog, StudioNameIndex names)
    {
        int count = catalog.Entries.Count;
        var rows = new BrowserAsset[count];
        var packages = new HashSet<string>();
        var lists = StudioCategories.All.ToDictionary(category => category.Name, _ => new List<BrowserAsset>(), StringComparer.Ordinal);
        var classes = Enum.GetValues<PickClass>().ToDictionary(kind => kind, _ => new List<BrowserAsset>());
        var categoryMap = new Dictionary<uint, string>();
        var owners = new Dictionary<uint, uint>();
        for (int index = 0; index < count; index++)
        {
            if ((index & 8191) == 0)
            {
                OperationToken.ThrowIfCancellationRequested();
                ReportProgress("Preparing asset browser", index, count);
            }
            var entry = catalog.Entries[index];
            var row = rows[index] = CreateRow(entry, names);
            packages.Add(entry.PackageName);
            if (row.InCategory) { lists[row.Category].Add(row); IndexCategory(categoryMap, owners, row, add: true); }
            if (ClassOf(entry) is { } kind) classes[kind].Add(row);
        }
        var assets = rows;
        if (lists.Values.Any(list => list.Count != 0))
        {
            assets = new BrowserAsset[count];
            int next = 0;
            foreach (var category in StudioCategories.All)
            {
                // A stable sort, so rows of equal rank keep catalog order.
                lists[category.Name] = [.. lists[category.Name].OrderBy(row => StudioCategories.RowRank(row, names))];
                lists[category.Name].CopyTo(assets, next);
                next += lists[category.Name].Count;
            }
            foreach (var row in rows) if (!row.InCategory) assets[next++] = row;
        }
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var components = new Dictionary<uint, int>();
        for (int index = 0; index < count; index++)
        {
            var row = assets[index];
            kinds[row.Kind] = kinds.GetValueOrDefault(row.Kind) + 1;
            if (IsComponentRow(row.Entry)) components[row.Entry.TagHash] = index;
        }
        ReportProgress("Preparing asset browser", count, count);
        return new(assets, ["All packages", .. packages.Order()], kinds, components,
            lists.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal),
            categoryMap, owners, classes.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
    }

    /** Membership changes copy installed rows without rereading their package payloads. */
    private void RefreshAddedBrowserRows()
    {
        if (Catalog is not { } catalog || Names is not { } names) return;
        var entries = nativeAdditions.OrderBy(pair => pair.Key)
            .Select(pair => (Entry: catalog.TryGetEntry(pair.Key, out var entry) ? entry
                : throw new InvalidDataException("A project browser entry is missing from the overlay."), Name: pair.Value.Name))
            .ToArray();
        if (addedBrowserEntries.AsSpan().SequenceEqual(entries)) return;
        var updated = new BrowserAsset[installedBrowserCount + entries.Length];
        Array.Copy(Assets, updated, installedBrowserCount);
        var changes = new List<(BrowserAsset? Old, BrowserAsset? New)>();
        for (int index = installedBrowserCount; index < Assets.Length; index++)
        {
            var old = Assets[index];
            kindCounts[old.Kind]--;
            componentRows.Remove(old.Entry.TagHash);
            changes.Add((old, null));
        }
        for (int index = 0; index < entries.Length; index++)
        {
            var entry = entries[index].Entry;
            var row = CreateRow(entry, names);
            int target = installedBrowserCount + index;
            updated[target] = row;
            kindCounts[row.Kind] = kindCounts.GetValueOrDefault(row.Kind) + 1;
            if (IsComponentRow(entry)) componentRows[entry.TagHash] = target;
            changes.Add((null, row));
        }
        UpdateCategoryRows(changes);
        var classes = new Dictionary<PickClass, BrowserAsset[]>();
        foreach (var (kind, installed) in installedClassCounts)
        {
            var held = classRows.GetValueOrDefault(kind) ?? [];
            classes[kind] = [.. held.Take(installed), .. updated.Skip(installedBrowserCount).Where(row => ClassOf(row.Entry) == kind)];
        }
        classRows = classes;
        PublishBrowserKinds();
        BrowserPackages = ["All packages", .. installedBrowserPackages.Skip(1)
            .Concat(entries.Select(row => row.Entry.PackageName)).Distinct().Order()];
        Assets = updated;
        addedBrowserEntries = entries;
        AssetRevision++;
    }
}
