using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** One page of the reference library: a .md file with front matter (url, title, tags) and an index.json row. */
public sealed record ReferencePage(string Path, string Title, string? Url, IReadOnlyList<string> Tags, string? Summary);

/**
 * The reference library (08-docs\reference): one Markdown file per saved doc or repo page, plus index.json. It is read
 * at call time and may still be filling in, so a half-written index or page is skipped, never fatal. Read-only.
 */
public static class ReferenceLibrary
{
    private const long MaximumFile = 4 << 20;

    /** SOLFORGE_REFERENCE, else the first '08-docs\reference' (or 'reference' beside 08-docs) above any start folder. */
    public static string? Locate(IEnumerable<string?> starts)
    {
        if (Environment.GetEnvironmentVariable("SOLFORGE_REFERENCE") is { Length: > 0 } set && Directory.Exists(set)) return Path.GetFullPath(set);
        foreach (string? start in starts)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            string? folder;
            try { folder = Directory.Exists(start) ? Path.GetFullPath(start) : null; }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            for (int depth = 0; folder is not null && depth < 12; depth++, folder = Directory.GetParent(folder)?.FullName)
            {
                string candidate = System.IO.Path.Combine(folder, "08-docs", "reference");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /** index.json rows first, then every .md file the index does not list yet, read from its front matter. */
    public static IReadOnlyList<ReferencePage> Load(string folder)
    {
        var pages = new Dictionary<string, ReferencePage>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in ReadIndex(folder)) pages.TryAdd(page.Path, page);
        foreach (string file in MarkdownFiles(folder))
        {
            string relative = System.IO.Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (pages.TryGetValue(relative, out var indexed) && indexed.Url is not null && indexed.Title.Length > 0) continue;
            var (front, _) = Split(SafeRead(file) ?? "");
            var fromFile = new ReferencePage(relative, front.GetValueOrDefault("title") ?? System.IO.Path.GetFileNameWithoutExtension(file),
                front.GetValueOrDefault("url"), Tags(front.GetValueOrDefault("tags")), front.GetValueOrDefault("summary") ?? front.GetValueOrDefault("description"));
            pages[relative] = indexed is null ? fromFile : indexed with
            {
                Title = indexed.Title.Length > 0 ? indexed.Title : fromFile.Title, Url = indexed.Url ?? fromFile.Url,
                Tags = indexed.Tags.Count > 0 ? indexed.Tags : fromFile.Tags, Summary = indexed.Summary ?? fromFile.Summary
            };
        }
        // Index rows whose file is not written yet stay listed only when the file exists.
        return pages.Values.Where(page => File.Exists(System.IO.Path.Combine(folder, page.Path)))
            .OrderBy(page => page.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /** Ranked hits: title and tag words weigh most, then url, summary and body; snippet is the best body line. */
    public static JsonObject Search(string folder, string query, string? tag, int limit)
    {
        limit = Math.Clamp(limit, 1, 100);
        string[] words = Words(query);
        string phrase = string.Join(' ', words);
        var hits = new List<(ReferencePage Page, int Score, string Snippet)>();
        var pages = Load(folder);
        foreach (var page in pages)
        {
            if (tag is { Length: > 0 } && !page.Tags.Any(each => each.Equals(tag.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
            string body = Split(SafeRead(System.IO.Path.Combine(folder, page.Path)) ?? "").Body;
            string title = page.Title.ToLowerInvariant(), url = (page.Url ?? "").ToLowerInvariant(), summary = (page.Summary ?? "").ToLowerInvariant();
            string tags = string.Join(' ', page.Tags).ToLowerInvariant(), lower = body.ToLowerInvariant();
            int score = 0, matched = 0;
            foreach (string word in words)
            {
                int here = (title.Contains(word) ? 30 : 0) + (tags.Contains(word) ? 20 : 0) + (url.Contains(word) ? 10 : 0)
                    + (summary.Contains(word) ? 10 : 0) + 2 * Math.Min(5, Count(lower, word));
                if (here > 0) matched++;
                score += here;
            }
            if (words.Length > 1 && phrase.Length > 0)
            {
                if (title.Contains(phrase)) score += 50;
                if (lower.Contains(phrase)) score += 15;
            }
            if (words.Length == 0) score = 1;
            else if (matched * 2 < words.Length || score == 0) continue;
            hits.Add((page, score, Snippet(body, words, phrase)));
        }
        var rows = new JsonArray();
        foreach (var (page, score, snippet) in hits.OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Page.Title, StringComparer.OrdinalIgnoreCase).Take(limit))
            rows.Add(new JsonObject
            {
                ["title"] = page.Title, ["url"] = page.Url, ["path"] = page.Path, ["tags"] = new JsonArray(page.Tags.Select(each => (JsonNode?)each).ToArray()),
                ["summary"] = page.Summary, ["score"] = score, ["snippet"] = snippet
            });
        return new JsonObject { ["folder"] = folder, ["pages"] = pages.Count, ["count"] = rows.Count, ["hits"] = rows };
    }

    /** A page by library path or url, a slice of lines at a time. */
    public static JsonObject? Read(string folder, string? path, string? url, int offset, int limit)
    {
        string? relative = null;
        if (path is { Length: > 0 })
        {
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, path.Replace('\\', '/').TrimStart('/')));
            string root = System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
            relative = System.IO.Path.GetRelativePath(folder, full).Replace('\\', '/');
        }
        else if (url is { Length: > 0 })
            relative = Load(folder).FirstOrDefault(page => page.Url is { } known && SameUrl(known, url))?.Path;
        if (relative is null || SafeRead(System.IO.Path.Combine(folder, relative)) is not { } text) return null;
        var page = Load(folder).FirstOrDefault(each => each.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        offset = Math.Clamp(offset, 1, Math.Max(1, lines.Length));
        limit = Math.Clamp(limit, 1, 2000);
        int last = Math.Min(lines.Length, offset - 1 + limit);
        var slice = new StringBuilder();
        for (int i = offset - 1; i < last; i++) slice.Append(i + 1).Append('\t').AppendLine(lines[i]);
        return new JsonObject
        {
            ["path"] = relative, ["title"] = page?.Title, ["url"] = page?.Url,
            ["tags"] = page is null ? null : new JsonArray(page.Tags.Select(each => (JsonNode?)each).ToArray()),
            ["lines"] = lines.Length, ["from"] = offset, ["to"] = last, ["next"] = last < lines.Length ? (int?)(last + 1) : null, ["text"] = slice.ToString()
        };
    }

    private static IEnumerable<ReferencePage> ReadIndex(string folder)
    {
        string file = System.IO.Path.Combine(folder, "index.json");
        JsonNode? root;
        try { root = SafeRead(file) is { } text ? JsonNode.Parse(text) : null; }
        catch (JsonException) { root = null; } // Being written right now.
        var rows = root as JsonArray ?? (root as JsonObject)?.Select(pair => pair.Value).OfType<JsonArray>().FirstOrDefault();
        if (rows is null) yield break;
        foreach (var row in rows.OfType<JsonObject>())
        {
            string? path = (string?)Value(row, "path");
            if (path is not { Length: > 0 }) continue;
            path = path.Replace('\\', '/');
            if (System.IO.Path.IsPathRooted(path)) path = System.IO.Path.GetRelativePath(folder, path).Replace('\\', '/');
            var tags = row["tags"] is JsonArray array ? array.Select(each => each?.ToString() ?? "").Where(each => each.Length > 0).ToArray()
                : Tags((string?)Value(row, "tags"));
            yield return new ReferencePage(path, (string?)Value(row, "title") ?? "", (string?)Value(row, "url"), tags, (string?)Value(row, "summary"));
        }
    }

    private static object? Value(JsonObject row, string name) => row[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /** Front matter between leading '---' lines as key: value pairs, and the rest. */
    private static (Dictionary<string, string> Front, string Body) Split(string text)
    {
        var front = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return (front, text);
        int end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (front, text);
        foreach (string line in normalized[4..end].Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0])) continue;
            front[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('"', '\'');
        }
        int bodyStart = normalized.IndexOf('\n', end + 4);
        return (front, bodyStart < 0 ? "" : normalized[(bodyStart + 1)..]);
    }

    private static string[] Tags(string? text) => (text ?? "").Trim('[', ']')
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(tag => tag.Trim('"', '\'')).Where(tag => tag.Length > 0).ToArray();

    private static string[] Words(string query) => query.ToLowerInvariant()
        .Split([' ', ',', ';', '"', '\'', '(', ')', '?', '!'], StringSplitOptions.RemoveEmptyEntries).Where(word => word.Length >= 2).Distinct().ToArray();

    private static int Count(string text, string word)
    {
        int count = 0;
        for (int at = text.IndexOf(word, StringComparison.Ordinal); at >= 0 && count < 5; at = text.IndexOf(word, at + word.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    private static string Snippet(string body, string[] words, string phrase)
    {
        string[] lines = body.Split('\n');
        string best = "";
        int bestScore = 0;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string lower = line.ToLowerInvariant();
            int score = words.Count(word => lower.Contains(word)) + (phrase.Length > 0 && lower.Contains(phrase) ? words.Length : 0);
            if (score > bestScore) { bestScore = score; best = line; }
        }
        if (best.Length == 0) best = lines.Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0 && !line.StartsWith('#')) ?? "";
        return best.Length > 240 ? best[..240] + "..." : best;
    }

    private static bool SameUrl(string a, string b)
    {
        static string Clean(string url) => url.Trim().TrimEnd('/').Replace("http://", "https://", StringComparison.OrdinalIgnoreCase)
            .Replace("://www.", "://", StringComparison.OrdinalIgnoreCase);
        return string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafeRead(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaximumFile) return null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<string> MarkdownFiles(string folder)
    {
        string[] files;
        try { files = Directory.GetFiles(folder, "*.md", SearchOption.AllDirectories); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { files = []; }
        return files.Order(StringComparer.OrdinalIgnoreCase);
    }
}
