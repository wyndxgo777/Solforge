using System.Runtime.CompilerServices;
using SunriseStudio.Packages;
using SunriseStudio.Scene.Collision;

namespace SunriseStudio.Authoring;

/** Installed owner links are immutable; project owner edits replace those links per query. */
internal sealed class CollisionReferenceIndex
{
    private static readonly ConditionalWeakTable<PackageCatalog, CollisionReferenceIndex> Cache = new();
    private readonly object gate = new();
    private string? identity;
    private Dictionary<uint, StaticCollisionOwner> owners = [];
    private Dictionary<uint, HashSet<uint>> reverse = [];

    /** Revalidates the catalog's cached reference index, if one was built, against overlay changes. */
    internal static void Check(PackageCatalog catalog)
    {
        if (!Cache.TryGetValue(catalog, out var index)) return;
        lock (index.gate) index.Validate(catalog);
    }

    /** Null changedTags means the caller has not supplied a complete raw-delta change set. */
    internal static IReadOnlyList<StaticCollisionOwner> Find(PackageCatalog catalog, IReadOnlySet<uint> shapes,
        Func<uint, byte[]> readRaw, IReadOnlyCollection<uint>? changedTags)
    {
        var index = Cache.GetValue(catalog, _ => new());
        lock (index.gate)
        {
            index.Validate(catalog);
            var changed = changedTags is null ? index.owners.Keys.ToHashSet() : changedTags.ToHashSet();
            changed.UnionWith(catalog.GetOverlayEntries().Where(entry => entry.Entry.Reference == StaticCollisionReader.OwnerClass)
                .Select(entry => entry.Entry.TagHash));
            var candidates = new SortedDictionary<uint, StaticCollisionOwner>();
            foreach (uint shape in shapes)
                if (index.reverse.TryGetValue(shape, out var links))
                    foreach (uint owner in links)
                        if (!changed.Contains(owner)) candidates.TryAdd(owner, index.owners[owner]);
            foreach (uint tag in changed)
            {
                if (!catalog.TryGetEntry(tag, out var entry) || entry.Reference != StaticCollisionReader.OwnerClass) continue;
                var owner = StaticCollisionReader.ReadOwner(tag, readRaw(tag));
                if (owner.ImportTags.Any(shapes.Contains)) candidates[tag] = owner;
            }
            return candidates.Values.ToArray();
        }
    }

    /** Changed package tables require reopening the catalog before any indexed write. */
    private void Validate(PackageCatalog catalog)
    {
        string current = catalog.GetPackageFileIdentity();
        if (identity is not null)
        {
            if (identity != current) throw new InvalidDataException("Packages changed after collision indexing. Reopen the installation.");
            return;
        }
        var next = new Dictionary<uint, StaticCollisionOwner>();
        var links = new Dictionary<uint, HashSet<uint>>();
        foreach (var entry in catalog.Entries.Where(entry => entry.Reference == StaticCollisionReader.OwnerClass))
        {
            var owner = StaticCollisionReader.ReadOwner(entry.TagHash, catalog.ReadOriginal(entry.TagHash));
            next.Add(owner.Tag, owner);
            foreach (uint shape in owner.ImportTags.Distinct())
            {
                if (!links.TryGetValue(shape, out var users)) links[shape] = users = [];
                users.Add(owner.Tag);
            }
        }
        if (catalog.GetPackageFileIdentity() != current)
            throw new InvalidDataException("Packages changed while collision references were read. Reopen the installation.");
        owners = next; reverse = links; identity = current;
    }
}
