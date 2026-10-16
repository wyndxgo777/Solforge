using Solforge.Mcp.Data;
using SunriseStudio.Packages;

namespace SunriseStudio.Editor;

/**
 * The package-data side of the MCP data tools (get_string, dump_tag, infer_tag_schema...): raw tag bytes, class
 * counts and localized strings over the open installation. Reads take no UI-thread hop; PackageCatalog lookups are
 * lock-free and Read takes its own lock. Strings load once per catalog on first use.
 */
public sealed partial class StudioForm
{
    private sealed partial class AiHost : ISolforgeDataHost
    {
        private readonly object dataLock = new();
        private PackageCatalog? dataCatalog;
        private Task<LocalizedStrings>? dataStrings;
        private IReadOnlyDictionary<uint, int>? dataClassCounts;

        private PackageCatalog? DataCatalog => Owner.workspace.Catalog;

        public string? DataUnavailable => DataCatalog is null ? "No game installation is open in Solforge yet." : null;

        public TagSummary? Tag(uint hash)
        {
            if (DataCatalog is not { } catalog || !catalog.TryGetEntry(hash, out var entry)) return null;
            return new TagSummary(hash, catalog.GetName(hash), entry.Reference, ClassNames.Describe(entry.Reference), entry.PackageName, entry.Size);
        }

        public bool TagExists(uint hash) => DataCatalog?.ContainsTag(hash) == true;

        public byte[]? ReadTag(uint hash, bool original)
        {
            if (DataCatalog is not { } catalog || !catalog.ContainsTag(hash)) return null;
            try { return original ? catalog.ReadOriginal(hash) : catalog.Read(hash); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException) { return null; }
        }

        public IEnumerable<TagSummary> TagsOfClass(uint classHash)
        {
            if (DataCatalog is not { } catalog) yield break;
            foreach (var entry in catalog.Entries)
                if (entry.Reference == classHash)
                    yield return new TagSummary(entry.TagHash, catalog.GetName(entry.TagHash), entry.Reference, ClassNames.Describe(entry.Reference), entry.PackageName, entry.Size);
        }

        public IReadOnlyDictionary<uint, string> ClassLabels => ClassNames.All;

        public IReadOnlyDictionary<uint, int> ClassCounts()
        {
            var catalog = DataCatalog;
            lock (dataLock)
            {
                if (catalog is null) return new Dictionary<uint, int>();
                if (ReferenceEquals(catalog, dataCatalog) && dataClassCounts is { } counts) return counts;
                Reset(catalog);
                return dataClassCounts = catalog.Entries.GroupBy(entry => entry.Reference).ToDictionary(group => group.Key, group => group.Count());
            }
        }

        private void Reset(PackageCatalog catalog)
        {
            if (ReferenceEquals(catalog, dataCatalog)) return;
            dataCatalog = catalog; dataStrings = null; dataClassCounts = null;
        }

        private Task<LocalizedStrings> Strings(CancellationToken cancel)
        {
            var catalog = DataCatalog ?? throw new InvalidOperationException("No game installation is open in Solforge yet.");
            lock (dataLock)
            {
                Reset(catalog);
                return dataStrings ??= Task.Run(() => LocalizedStrings.Load(catalog, CancellationToken.None), CancellationToken.None);
            }
        }

        public async Task<IReadOnlyList<string>> StringsAsync(uint hash, CancellationToken cancel)
        {
            var strings = await Strings(cancel).WaitAsync(cancel).ConfigureAwait(false);
            return strings.Get(hash) is { } text ? [text] : strings.Candidates(hash);
        }

        public async Task<IReadOnlyList<(uint Hash, string Text, uint? Container)>> SearchStringsAsync(string query, int limit, CancellationToken cancel)
        {
            var strings = await Strings(cancel).WaitAsync(cancel).ConfigureAwait(false);
            return [.. strings.Search(query, limit).Select(match => (Hash: match.Hash, Text: match.Text, Container: match.ContainerTag))];
        }

        public string? StringIfLoaded(uint hash)
        {
            Task<LocalizedStrings>? task;
            lock (dataLock) task = ReferenceEquals(dataCatalog, DataCatalog) ? dataStrings : null;
            return task is { IsCompletedSuccessfully: true } ? task.Result.Get(hash) : null;
        }
    }
}
