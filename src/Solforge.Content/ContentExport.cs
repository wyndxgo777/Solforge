/**
 * File: Exports content documents as plain-data Lua modules for the server settings; used by the export_content tool
 * and Packaging.
 */
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solforge.Content;

/** Writes plain-data Lua (no code, no globals): what the server's settings modules load. */
public static class LuaData
{
    /** Renders a JSON value as a Lua module with a header comment. */
    public static string Module(JsonNode? value, string header)
    {
        var text = new StringBuilder();
        foreach (string line in header.Split('\n')) text.Append("-- ").Append(line).Append('\n');
        text.Append("return ");
        Write(text, value, 0);
        text.Append('\n');
        return text.ToString();
    }

    /** Writes a JSON value as a Lua table or literal at a nesting depth. */
    public static void Write(StringBuilder text, JsonNode? value, int depth)
    {
        switch (value)
        {
            case null: text.Append("nil"); break;
            case JsonObject members:
                if (members.Count == 0) { text.Append("{}"); break; }
                text.Append("{\n");
                foreach (var (key, member) in members.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (member is null) continue;
                    Indent(text, depth + 1);
                    text.Append(IsIdentifier(key) ? key : "[" + Quote(key) + "]").Append(" = ");
                    Write(text, member, depth + 1);
                    text.Append(",\n");
                }
                Indent(text, depth); text.Append('}');
                break;
            case JsonArray items:
                if (items.Count == 0) { text.Append("{}"); break; }
                text.Append("{\n");
                foreach (var item in items) { Indent(text, depth + 1); Write(text, item, depth + 1); text.Append(",\n"); }
                Indent(text, depth); text.Append('}');
                break;
            case JsonValue scalar:
                // Parsed and constructed values both answer GetValueKind; numbers keep their JSON text.
                text.Append(scalar.GetValueKind() switch
                {
                    JsonValueKind.String => Quote(scalar.GetValue<string>()),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => scalar.ToJsonString(),
                    _ => "nil"
                });
                break;
        }
    }

    private static readonly HashSet<string> Keywords =
    [
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in", "local", "nil", "not", "or",
        "repeat", "return", "then", "true", "until", "while"
    ];

    /** Whether a key can be written as a bare Lua identifier (not a keyword). */
    public static bool IsIdentifier(string key) => key.Length > 0 && !Keywords.Contains(key) && (char.IsAsciiLetter(key[0]) || key[0] == '_')
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /** Quotes text as a Lua string literal. */
    public static string Quote(string text)
    {
        var quoted = new StringBuilder("\"");
        foreach (char c in text)
            quoted.Append(c switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                < ' ' => "\\" + ((int)c).ToString("D3", CultureInfo.InvariantCulture),
                _ => c.ToString()
            });
        return quoted.Append('"').ToString();
    }

    /** Appends two spaces per depth level. */
    private static void Indent(StringBuilder text, int depth) => text.Append(' ', depth * 2);
}

/** One exported file (relative to the output folder) with its text. */
public sealed record ExportedFile(string RelativePath, string Text);

/**
 * Builds the server-side files from a project's content: server/scripts/loot_pools.lua (live),
 * server/data/subclass_nodes.tsv (live names) and server/scripts/sol_custom_data.lua (every document as
 * plain data: the server's custom data reader, server/activity/authored_content, and mission scripts read it).
 * Not sol_content: that name is the server's content-access gate (sol-server src/sol_content.h).
 */
public static class ContentExport
{
    /** The custom data module, relative to the export folder; the server reads Sunrise\scripts\sol_custom_data.lua. */
    public const string CustomDataPath = "server/scripts/sol_custom_data.lua";

    /** Builds the export files (path and text) for a set of documents. */
    public static IReadOnlyList<ExportedFile> Build(IReadOnlyList<ContentDocument> documents)
    {
        var files = new List<ExportedFile>();
        var pools = documents.OfType<LootPoolDefinition>().OrderBy(pool => pool.Id, StringComparer.Ordinal).ToList();
        if (pools.Count > 0) files.Add(new("server/scripts/loot_pools.lua", LootPoolsModule(pools)));
        var subclasses = documents.OfType<SubclassDefinition>().OrderBy(subclass => subclass.Id, StringComparer.Ordinal).ToList();
        if (subclasses.Count > 0) files.Add(new("server/data/subclass_nodes.tsv", SubclassInterop.NodeNamesTsv(subclasses)));
        if (documents.Count > 0)
        {
            var all = new JsonObject();
            foreach (var family in ContentFamilies.All)
            {
                var members = new JsonObject();
                foreach (var document in documents.Where(document => document.Family == family.Id).OrderBy(document => document.Id, StringComparer.Ordinal))
                {
                    var node = ContentJson.ToNode(document);
                    node.Remove("unknown");
                    members[document.Id] = node;
                }
                if (members.Count > 0) all[family.Id] = members;
            }
            all["schema"] = ContentDocument.CurrentSchema;
            files.Add(new(CustomDataPath, LuaData.Module(all,
                "Project Sol custom data written by Solforge (content/<family>/<id>.json). Plain data: no code runs.\n" +
                "Copy to Sunrise\\scripts\\sol_custom_data.lua beside the server; it is re-read when it changes (/content status).\n" +
                "Live parts per family: Solforge list_content_families. Mission scripts may require(\"sol_custom_data\").")));
        }
        return files;
    }

    /** The loot_pools settings module (server/loot/loot_pools.cpp layer_module). */
    public static string LootPoolsModule(IEnumerable<LootPoolDefinition> pools)
    {
        var poolTable = new JsonObject();
        var rules = new JsonObject();
        foreach (var pool in pools)
        {
            var table = new JsonObject();
            void Weights(string key, Dictionary<string, double> weights)
            {
                if (weights.Count == 0) return;
                var list = new JsonObject();
                foreach (var (hash, weight) in weights.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    list[ContentDocument.ParseHash(hash) is { } value ? ContentDocument.Hex(value) : hash] = weight;
                table[key] = list;
            }
            Weights("any", pool.Any); Weights("titan", pool.Titan); Weights("hunter", pool.Hunter); Weights("warlock", pool.Warlock);
            if (pool.Only.Count > 0)
                table["only"] = new JsonArray(pool.Only.Select(hash => (JsonNode)(ContentDocument.ParseHash(hash) is { } value ? ContentDocument.Hex(value) : hash)).ToArray());
            if (pool.Catalog is not null) table["catalog"] = pool.Catalog;
            if (pool.Quantity != 1) table["quantity"] = pool.Quantity;
            if (pool.Profile is { } profile) table["profile"] = profile;
            poolTable[pool.Id] = table;
            foreach (var rule in pool.EnemyKill)
            {
                var row = new JsonObject { ["pool"] = pool.Id, ["chance"] = rule.Chance };
                if (rule.Activity.Length > 0) row["activity"] = rule.Activity;
                if (rule.Bubble.Length > 0) row["bubble"] = rule.Bubble;
                if (rule.BubbleHash is not null && ContentDocument.ParseHash(rule.BubbleHash) is { } bubble) row["bubble_hash"] = ContentDocument.Hex(bubble);
                if (rule.Region.Count == 1) row["region"] = rule.Region[0];
                else if (rule.Region.Count > 1) row["region"] = new JsonArray(rule.Region.Select(region => (JsonNode)region).ToArray());
                rules[rule.Id] = row;
            }
        }
        var root = new JsonObject { ["pools"] = poolTable };
        if (rules.Count > 0) root["enemy_kill"] = rules;
        return LuaData.Module(root, "loot_pools settings module, written by Solforge from content/loot_pool.\n" +
            "Copy to Sunrise\\scripts\\loot_pools.lua beside the server; it is re-read when it changes.\n" +
            "Pools layer over the built-in ones by id; weight 0 removes an item.");
    }

    /** Writes the files under outputRoot (atomic per file) and returns their full paths. */
    public static IReadOnlyList<string> WriteTo(string outputRoot, IReadOnlyList<ExportedFile> files)
    {
        var written = new List<string>();
        string root = Path.GetFullPath(outputRoot);
        foreach (var file in files)
        {
            string path = Path.GetFullPath(Path.Combine(root, file.RelativePath));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"{file.RelativePath} leaves the output folder.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", file.Text, new UTF8Encoding(false));
            File.Move(path + ".tmp", path, overwrite: true);
            written.Add(path);
        }
        return written;
    }
}
