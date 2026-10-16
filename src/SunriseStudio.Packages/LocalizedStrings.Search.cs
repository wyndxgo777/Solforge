namespace SunriseStudio.Packages;

/** One search hit: string hash, text and the container it came from, if local. */
public sealed record LocalizedStringMatch(uint Hash, string Text, uint? ContainerTag = null);

/** Search half of LocalizedStrings: text search globally or within one container. */
public sealed partial class LocalizedStrings
{
    /**
     * Global searches return unambiguous text; a supplied container keeps its local meanings.
     * @param query Case-insensitive text fragment.
     * @param limit Result limit, bounded to two hundred rows.
     * @param containerTag Optional bank for context-specific strings.
     * @return Matching owned text without combining distinct bank-local meanings.
     */
    public IReadOnlyList<LocalizedStringMatch> Search(string query, int limit = 50, uint? containerTag = null)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        var source = containerTag is { } tag ? byContainer.GetValueOrDefault(tag) : unique;
        if (source is null) return [];
        int count = Math.Clamp(limit, 1, 200);
        // Sort before the limit: prefix matches first, then shorter text.
        return source.Where(pair => pair.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Value.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(pair => pair.Value.Length).ThenBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key).Take(count)
            .Select(pair => new LocalizedStringMatch(pair.Key, pair.Value, containerTag)).ToArray();
    }
}
