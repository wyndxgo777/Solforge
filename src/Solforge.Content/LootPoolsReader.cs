using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Solforge.Content;

/** One place a loot pool pays an item: pool id, class list (any/titan/hunter/warlock/only/catalog), weight and the drop rules that roll it. */
public sealed record LootPoolHit(string Pool, string List, double Weight, int PoolSize, int Quantity, bool Profile,
    IReadOnlyList<string> Rules, string File);

/**
 * Reads the server's loot_pools settings module (Sunrise\scripts\loot_pools.lua, and the module Solforge writes from
 * content/loot_pool) back into JSON, and finds the pools that pay one item. The reader takes the data-only Lua these
 * files use: tables, ["key"] = and key = fields, strings, numbers, booleans, nil and -- comments. Function calls or
 * expressions make it throw InvalidDataException.
 */
public static class LootPoolsReader
{
    /** Parses a "return { ... }" Lua data module into JSON (arrays for tables with only positional fields). */
    public static JsonNode? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = Tokens(text).ToList();
        int at = 0;
        if (at < tokens.Count && tokens[at] == "return") at++;
        var value = Value(tokens, ref at);
        if (at < tokens.Count) throw new InvalidDataException($"Unexpected '{tokens[at]}' after the module table.");
        return value;
    }

    /**
     * Pools that pay itemHash: listed with a weight above 0 under any/titan/hunter/warlock, named under only, or a
     * catalog pool when inCatalog says the item belongs to that catalog (exotic_weapons). Later modules override earlier
     * ones by pool id and item (weight 0 removes), the way the server layers overlays over the built-in pools.
     */
    public static IReadOnlyList<LootPoolHit> Find(IEnumerable<(JsonNode? Module, string File)> modules, uint itemHash,
        Func<string, bool>? inCatalog = null)
    {
        var pools = new Dictionary<string, (JsonObject Pool, string File)>(StringComparer.Ordinal);
        var rules = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (module, file) in modules)
        {
            if (module is not JsonObject root) continue;
            if (root["pools"] is JsonObject layer)
                foreach (var (id, node) in layer)
                {
                    if (node is not JsonObject layerPool) continue;
                    var pool = Canonical(layerPool);
                    if (!pools.TryGetValue(id, out var existing)) { pools[id] = (pool, file); continue; }
                    foreach (var (key, value) in pool)
                    {
                        if (value is JsonObject weights && existing.Pool[key] is JsonObject baseWeights)
                            foreach (var (hash, weight) in weights) baseWeights[hash] = weight?.DeepClone();
                        else existing.Pool[key] = value?.DeepClone();
                    }
                    pools[id] = (existing.Pool, file);
                }
            if (root["enemy_kill"] is JsonObject kills)
                foreach (var (name, rule) in kills)
                    if (rule is JsonObject row) rules[name] = row; else rules.Remove(name);
        }
        var hits = new List<LootPoolHit>();
        foreach (var (id, (pool, file)) in pools.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            int quantity = pool["quantity"] is JsonValue q && q.TryGetValue(out double qd) ? (int)qd : 1;
            bool profile = pool["profile"] is JsonValue p && p.TryGetValue(out bool pb) && pb;
            var poolRules = rules.Where(pair => (string?)pair.Value["pool"] == id).Select(pair => Rule(pair.Key, pair.Value)).ToArray();
            void Add(string list, double weight, int size) => hits.Add(new(id, list, weight, size, quantity, profile, poolRules, file));
            if (pool["only"] is JsonArray only && only.Count > 0)
            {
                if (only.Any(entry => ParseHash(entry) == itemHash)) Add("only", 1, only.Count);
                continue;
            }
            foreach (string list in new[] { "any", "titan", "hunter", "warlock" })
            {
                if (pool[list] is not JsonObject weights) continue;
                int size = weights.Count(pair => Weight(pair.Value) > 0);
                foreach (var (hash, weight) in weights)
                    if (ParseHash(hash) == itemHash && Weight(weight) > 0) Add(list, Weight(weight), size);
            }
            if (pool["catalog"] is JsonValue catalog && catalog.TryGetValue(out string? name) && inCatalog?.Invoke(name) == true)
                Add("catalog:" + name, 1, 0);
        }
        return hits;
    }

    /** An item hash key: "0x..." hex or decimal (the server writes decimal strings, Solforge writes 0x hex). */
    public static uint? ParseHash(object? value)
    {
        string? text = value switch { JsonValue json when json.TryGetValue(out string? s) => s, JsonValue json when json.TryGetValue(out double d) => d.ToString("R", CultureInfo.InvariantCulture), string s => s, _ => null };
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex) ? hex : null;
        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint number) ? number : null;
    }

    /** A copy of a pool whose weight lists key by decimal hash, so "0x00000001" and "1" layer as one item. */
    private static JsonObject Canonical(JsonObject pool)
    {
        var copy = (JsonObject)pool.DeepClone();
        foreach (string list in new[] { "any", "titan", "hunter", "warlock" })
        {
            if (copy[list] is not JsonObject weights) continue;
            var keyed = new JsonObject();
            foreach (var (hash, weight) in weights)
                keyed[ParseHash(hash)?.ToString(CultureInfo.InvariantCulture) ?? hash] = weight?.DeepClone();
            copy[list] = keyed;
        }
        return copy;
    }

    private static double Weight(JsonNode? node) => node is JsonValue value && value.TryGetValue(out double weight) ? weight : 0;

    private static string Rule(string name, JsonObject rule)
    {
        var text = new StringBuilder(name);
        if ((string?)rule["activity"] is { Length: > 0 } activity) text.Append(" activity~").Append(activity);
        if ((string?)rule["bubble"] is { Length: > 0 } bubble) text.Append(" bubble~").Append(bubble);
        if (rule["bubble_hash"] is JsonNode hash) text.Append(" bubble_hash=").Append(hash.ToString());
        if (rule["region"] is JsonNode region) text.Append(" region=").Append(region.ToJsonString());
        text.Append(" chance=").Append(rule["chance"] is JsonValue chance && chance.TryGetValue(out double c) ? c.ToString("0.###", CultureInfo.InvariantCulture) : "1");
        return text.ToString();
    }

    private static JsonNode? Value(List<string> tokens, ref int at)
    {
        if (at >= tokens.Count) throw new InvalidDataException("The module ends early.");
        string token = tokens[at++];
        if (token == "{") return Table(tokens, ref at);
        if (token.StartsWith('"')) return JsonValue.Create(token[1..]);
        if (token is "true" or "false") return JsonValue.Create(token == "true");
        if (token == "nil") return null;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return JsonValue.Create(number);
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
            return JsonValue.Create((double)hex);
        throw new InvalidDataException($"Unexpected '{token}' in the loot pools module.");
    }

    private static JsonNode Table(List<string> tokens, ref int at)
    {
        var keyed = new JsonObject();
        var positional = new JsonArray();
        while (true)
        {
            if (at >= tokens.Count) throw new InvalidDataException("A table is not closed.");
            if (tokens[at] == "}") { at++; break; }
            string? key = null;
            if (tokens[at] == "[")
            {
                at++;
                var keyValue = Value(tokens, ref at);
                if (at + 1 >= tokens.Count || tokens[at] != "]" || tokens[at + 1] != "=")
                    throw new InvalidDataException("Expected '] =' after a table key.");
                key = keyValue switch { JsonValue v when v.TryGetValue(out string? s) => s, JsonValue v => v.ToJsonString(), _ => "nil" };
                at += 2;
            }
            else if (at + 1 < tokens.Count && tokens[at + 1] == "=" && IsIdentifier(tokens[at]))
            {
                key = tokens[at];
                at += 2;
            }
            var value = Value(tokens, ref at);
            if (key is null) positional.Add(value);
            else keyed[key] = value;
            if (at < tokens.Count && tokens[at] is "," or ";") at++;
        }
        if (keyed.Count == 0 && positional.Count > 0) return positional;
        for (int i = 0; i < positional.Count; i++) keyed[(i + 1).ToString(CultureInfo.InvariantCulture)] = positional[i]?.DeepClone();
        return keyed;
    }

    private static bool IsIdentifier(string token) => token.Length > 0 && (char.IsLetter(token[0]) || token[0] == '_')
        && token.All(c => char.IsLetterOrDigit(c) || c == '_');

    /** Tokens: punctuation, identifiers/numbers, and strings returned as '"' + unescaped text. */
    private static IEnumerable<string> Tokens(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                if (i + 3 < text.Length && text[i + 2] == '[' && text[i + 3] == '[')
                {
                    int end = text.IndexOf("]]", i + 4, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                }
                else { int end = text.IndexOf('\n', i); i = end < 0 ? text.Length : end + 1; }
                continue;
            }
            if (c is '{' or '}' or '[' or ']' or '=' or ',' or ';') { yield return c.ToString(); i++; continue; }
            if (c is '"' or '\'')
            {
                var value = new StringBuilder("\"");
                i++;
                while (i < text.Length && text[i] != c)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        value.Append(text[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => text[i] });
                    }
                    else value.Append(text[i]);
                    i++;
                }
                if (i >= text.Length) throw new InvalidDataException("A string is not closed.");
                i++;
                yield return value.ToString();
                continue;
            }
            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.' or '-' or '+')) i++;
            if (i == start) throw new InvalidDataException($"Unexpected '{c}' in the loot pools module.");
            yield return text[start..i];
        }
    }
}
