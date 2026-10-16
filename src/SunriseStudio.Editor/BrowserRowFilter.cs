namespace SunriseStudio.Editor;

/** One row filter for the browser and the creation pickers. */
public static class BrowserRowFilter
{
    /** Arrays this short with no search words filter on the calling thread; the rest run on a worker. */
    public const int InlineRows = 50_000;

    /** Splits a search box string into words; empty entries are dropped. */
    public static string[] Words(string search) =>
        search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /**
     * Rows that pass keep and contain every word; rows keep their order.
     * @param keep Runs on a worker for large arrays, so it must not read packages.
     */
    public static BrowserAsset[] Filter(IReadOnlyList<BrowserAsset> rows, string[] words, Func<BrowserAsset, bool>? keep,
        CancellationToken token)
    {
        var result = new List<BrowserAsset>();
        for (int index = 0; index < rows.Count; index++)
        {
            if ((index & 511) == 0) token.ThrowIfCancellationRequested();
            var row = rows[index];
            if (keep is not null && !keep(row)) continue;
            if (words.Length != 0)
            {
                string text = row.SearchText;
                if (!words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
            }
            result.Add(row);
        }
        return [.. result];
    }

    /** Filters inline when cheap and returns the rows; otherwise returns the started worker. */
    public static (BrowserAsset[]? Rows, Task<BrowserAsset[]>? Work) Start(IReadOnlyList<BrowserAsset> rows, string[] words,
        Func<BrowserAsset, bool>? keep, CancellationToken token) =>
        words.Length == 0 && rows.Count < InlineRows
            ? (Filter(rows, words, keep, token), null)
            : (null, Task.Run(() => Filter(rows, words, keep, token), token));
}
