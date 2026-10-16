namespace SunriseStudio.Packages;

/** Refresh half of LocalizedStrings: updates only what changed in edited containers. */
public sealed partial class LocalizedStrings
{
    /** Unchanged bank maps remain shared; only edited containers and English data are decoded. */
    internal LocalizedStrings Refresh(PackageCatalog catalog, IReadOnlySet<uint> changes,
        out IReadOnlySet<uint> banks, out IReadOnlySet<uint> hashes)
    {
        var affected = englishData.Where(pair => changes.Contains(pair.Value)).Select(pair => pair.Key)
            .Concat(changes.Where(tag => byContainer.ContainsKey(tag) || catalog.TryGetEntry(tag, out var entry)
                && entry.Reference == ContainerClass)).ToHashSet();
        banks = affected;
        hashes = new HashSet<uint>();
        if (affected.Count == 0) return this;
        var updated = LoadBanks(catalog, affected);
        var result = new LocalizedStrings();
        foreach (var pair in byContainer) if (!affected.Contains(pair.Key)) result.byContainer.Add(pair.Key, pair.Value);
        foreach (var pair in updated.byContainer) result.byContainer.Add(pair.Key, pair.Value);
        foreach (var pair in englishData) if (!affected.Contains(pair.Key)) result.englishData.Add(pair.Key, pair.Value);
        foreach (var pair in updated.englishData) result.englishData.Add(pair.Key, pair.Value);
        foreach (var pair in bankErrors) if (!affected.Contains(pair.Key)) result.bankErrors.Add(pair.Key, pair.Value);
        foreach (var pair in updated.bankErrors) result.bankErrors.Add(pair.Key, pair.Value);
        var all = new Dictionary<uint, HashSet<string>>();
        foreach (var map in result.byContainer.Values)
            foreach (var (hash, text) in map)
            {
                if (!all.TryGetValue(hash, out var texts)) all.Add(hash, texts = new(StringComparer.Ordinal));
                texts.Add(text);
            }
        foreach (var (hash, texts) in all)
            if (texts.Count == 1) result.unique.Add(hash, texts.First());
            else result.ambiguous.Add(hash, texts.ToArray());
        hashes = affected.SelectMany(tag => (byContainer.GetValueOrDefault(tag)?.Keys.AsEnumerable() ?? [])
                .Concat(updated.byContainer.GetValueOrDefault(tag)?.Keys.AsEnumerable() ?? []))
            .Where(hash => Get(hash) != result.Get(hash)).ToHashSet();
        result.Diagnostics = result.bankErrors.Values.ToArray();
        result.FailedContainerCount = result.bankErrors.Count;
        return result;
    }
}
