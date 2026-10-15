using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Solforge.Mcp;

/** Where a name came from: on screen now, the Object tree of the open map, or the package asset index. */
public enum ObjectScope { Viewport, Tree, Asset }

/** One resolvable name. Map is null for assets, which belong to no map. */
public sealed record ObjectEntry(string Id, string Label, string Kind, ObjectScope Scope, string? Map = null,
    string? Package = null, uint Tag = 0, IReadOnlyList<uint>? Hashes = null);

public sealed record ObjectMatch(ObjectEntry Entry, int Score, string Why);

/**
 * Fuzzy resolution of spoken names ("static mesh 7489", "last city crater", "[182]", "0x80A1B2C3") against
 * viewport labels. Numbers are identities: a query number missing from a label costs more than any word
 * can earn back. Map-scoped like the Missions tab: objects of another map never match.
 */
public static class ObjectIndex
{
    public static IReadOnlyList<ObjectMatch> Find(IEnumerable<ObjectEntry> entries, string query, string? map = null,
        IReadOnlyDictionary<string, float>? recency = null, int limit = 10)
    {
        var parsed = Parse(query);
        if (parsed.Words.Count == 0 && parsed.Numbers.Count == 0 && parsed.Hex is null) return [];
        var matches = new List<ObjectMatch>();
        foreach (var entry in entries)
        {
            if (map is not null && entry.Map is not null && !string.Equals(entry.Map, map, StringComparison.OrdinalIgnoreCase)) continue;
            int score = Score(entry, parsed, out string why);
            if (score <= 0) continue;
            if (recency?.GetValueOrDefault(entry.Id) is > 0 and var recent) { score += (int)(80 * recent); why += ", seen recently"; }
            score += entry.Scope switch { ObjectScope.Viewport => 30, ObjectScope.Tree => 10, _ => 0 };
            matches.Add(new ObjectMatch(entry, score, why));
        }
        matches.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score)
            : string.CompareOrdinal(a.Entry.Label, b.Entry.Label));
        // The same object can be listed by more than one source; keep its best row.
        var seen = new HashSet<string>();
        return matches.Where(match => seen.Add(match.Entry.Id)).Take(limit).ToArray();
    }

    public static JsonArray ToJson(IEnumerable<ObjectMatch> matches)
    {
        var array = new JsonArray();
        foreach (var match in matches)
        {
            var row = new JsonObject
            {
                ["id"] = match.Entry.Id, ["label"] = match.Entry.Label, ["kind"] = match.Entry.Kind,
                ["source"] = match.Entry.Scope.ToString().ToLowerInvariant(), ["score"] = match.Score, ["why"] = match.Why
            };
            if (match.Entry.Map is not null) row["map"] = match.Entry.Map;
            if (match.Entry.Package is not null) row["package"] = match.Entry.Package;
            if (match.Entry.Tag != 0) row["tag"] = $"0x{match.Entry.Tag:X8}";
            if (match.Entry.Id.StartsWith("slot/", StringComparison.Ordinal)) row["next"] = "get_mission_object";
            array.Add(row);
        }
        return array;
    }

    private sealed record Query(string Raw, string Normalized, List<string> Words, List<string> Numbers, uint? Hex);

    private static Query Parse(string query)
    {
        uint? hex = null;
        var words = new List<string>();
        var numbers = new List<string>();
        foreach (string token in Tokens(query))
        {
            if (token.Length is >= 6 and <= 10 && (token.StartsWith("0x") || token.Any(c => c is >= 'a' and <= 'f'))
                && uint.TryParse(token.StartsWith("0x") ? token[2..] : token, NumberStyles.HexNumber, null, out uint value)
                && !token.All(char.IsLetter))
                hex = value;
            else if (token.All(char.IsDigit)) numbers.Add(token.TrimStart('0') is { Length: > 0 } trimmed ? trimmed : "0");
            else if (!StopWords.Contains(token)) words.Add(token);
        }
        return new Query(query.Trim(), Normalize(query), words, numbers, hex);
    }

    private static readonly HashSet<string> StopWords = ["the", "a", "an", "that", "this", "one", "do", "you", "see", "i", "is"];

    private static int Score(ObjectEntry entry, Query query, out string why)
    {
        why = "";
        // Exact ids (slot/80be91f7/000014/0014/0004, a world object id) win outright.
        if (string.Equals(entry.Id, query.Raw, StringComparison.OrdinalIgnoreCase)) { why = "exact id"; return 1200; }
        if (query.Hex is { } hex && entry.Tag == hex) { why = "tag hash"; return 900; }
        if (query.Hex is { } other && entry.Hashes?.Contains(other) == true) { why = "registry or asset hash"; return 850; }
        string label = Normalize(entry.Label);
        var tokens = Tokens(entry.Label + " " + entry.Kind).ToList();
        var numbers = tokens.Where(token => token.All(char.IsDigit)).Select(token => token.TrimStart('0') is { Length: > 0 } t ? t : "0").ToHashSet();
        int score = 0;
        var reasons = new List<string>();
        if (label == query.Normalized) { score += 1000; reasons.Add("exact label"); }
        else if (query.Normalized.Length >= 3 && label.Contains(query.Normalized, StringComparison.Ordinal)) { score += 400; reasons.Add("label contains query"); }
        foreach (string number in query.Numbers)
        {
            if (numbers.Contains(number)) { score += 200; reasons.Add("#" + number); }
            else score -= 400;
        }
        int wordHits = 0;
        foreach (string word in query.Words)
        {
            int best = 0;
            foreach (string token in tokens)
            {
                if (token.All(char.IsDigit)) continue;
                int value = token == word ? 60 : token.StartsWith(word, StringComparison.Ordinal) ? 40
                    : word.Length >= 4 && token.Contains(word, StringComparison.Ordinal) ? 25
                    : word.Length >= 4 && Math.Abs(token.Length - word.Length) <= 1 && Distance(token, word) <= 1 ? 20 : 0;
                best = Math.Max(best, value);
            }
            if (best > 0) wordHits++;
            score += best > 0 ? best : -40;
        }
        if (wordHits > 0) reasons.Add($"{wordHits}/{query.Words.Count} words");
        // A pure-word query must match at least half its words to count at all.
        if (query.Numbers.Count == 0 && query.Words.Count > 0 && wordHits * 2 < query.Words.Count && score < 400) return 0;
        why = string.Join(", ", reasons);
        return score;
    }

    private static IEnumerable<string> Tokens(string text)
    {
        var current = new StringBuilder();
        foreach (char c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { current.Append(c); continue; }
            if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static string Normalize(string text) => string.Join(' ', Tokens(text));

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
