/**
 * File: Reads and writes content documents as JSON files under the project content folder; ContentDocument.cs defines
 * the model.
 */
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Solforge.Content;

/** The one JSON shape every content file uses: snake_case members and enums, indented, nulls left out. */
public static class ContentJson
{
    /** The serializer options shared by every content file. */
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /** Serialises a document to JSON text. */
    public static string Serialize(ContentDocument document) =>
        JsonSerializer.Serialize(document, document.GetType(), Options).ReplaceLineEndings("\n") + "\n";

    /** Parses JSON text into the document class of a family. */
    public static ContentDocument Parse(string family, string json)
    {
        var type = ContentFamilies.Find(family)?.DocumentType ?? throw new ArgumentException($"Unknown content family '{family}'.");
        return (ContentDocument?)JsonSerializer.Deserialize(json, type, Options) ?? throw new InvalidDataException("The document is empty.");
    }

    /** A document as a JSON node. */
    public static JsonObject ToNode(ContentDocument document) =>
        JsonSerializer.SerializeToNode(document, document.GetType(), Options) as JsonObject ?? new JsonObject();

    /** RFC 7396 merge patch: members set to null are removed, objects merge, everything else replaces. */
    public static JsonObject MergePatch(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is null) target.Remove(key);
            else if (value is JsonObject child && target[key] is JsonObject existing) MergePatch(existing, child);
            else target[key] = value.DeepClone();
        }
        return target;
    }
}

/**
 * Content documents in a project: content/<family>/<id>.json. Writes are atomic (temp file then
 * move) so an editor watching the folder never reads half a file.
 */
public sealed class ContentStore(string projectRoot)
{
    public const string FolderName = "content";
    /** The content folder inside the project. */
    public string Root { get; } = Path.Combine(projectRoot, FolderName);

    /** The file path of a document (family folder plus id). */
    public string PathOf(string family, string id)
    {
        if (ContentFamilies.Find(family) is null) throw new ArgumentException($"Unknown content family '{family}'.");
        if (!ContentDocument.IsValidId(id)) throw new ArgumentException($"'{id}' is not a valid content id.");
        return Path.Combine(Root, family, id + ".json");
    }

    /** Lists stored documents as (family, id, path), optionally for one family. */
    public IReadOnlyList<(string Family, string Id, string Path)> List(string? family = null)
    {
        var rows = new List<(string, string, string)>();
        foreach (var known in ContentFamilies.All.Where(row => family is null || row.Id == family))
        {
            string folder = Path.Combine(Root, known.Id);
            if (!Directory.Exists(folder)) continue;
            foreach (string file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (ContentDocument.IsValidId(id)) rows.Add((known.Id, id, file));
            }
        }
        return rows;
    }

    /** Whether a document exists. */
    public bool Exists(string family, string id) => File.Exists(PathOf(family, id));

    /** Loads one document. */
    public ContentDocument Load(string family, string id)
    {
        string path = PathOf(family, id);
        if (!File.Exists(path)) throw new FileNotFoundException($"No {family} '{id}' in the project.", path);
        var document = ContentJson.Parse(family, File.ReadAllText(path));
        if (document.Id != id) throw new InvalidDataException($"{path} holds id '{document.Id}'; the file name says '{id}'.");
        return document;
    }

    /** Saves a document and returns its path. */
    public string Save(ContentDocument document)
    {
        string path = PathOf(document.Family, document.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, ContentJson.Serialize(document), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /** Deletes a document; false when it did not exist. */
    public bool Delete(string family, string id)
    {
        string path = PathOf(family, id);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /** Every document: parse errors, each document's own issues, then references to missing documents. */
    public IReadOnlyList<(string Family, string Id, ContentIssue Issue)> ValidateAll(string? family = null, string? onlyId = null)
    {
        var results = new List<(string, string, ContentIssue)>();
        var documents = new List<ContentDocument>();
        foreach (var (rowFamily, id, _) in List())
        {
            try { documents.Add(Load(rowFamily, id)); }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or NotSupportedException or ArgumentException)
            {
                if ((family is null || family == rowFamily) && (onlyId is null || onlyId == id)) results.Add((rowFamily, id, new("", error.Message)));
            }
        }
        var known = documents.Select(document => (document.Family, document.Id)).ToHashSet();
        foreach (var document in documents.Where(document => (family is null || document.Family == family) && (onlyId is null || document.Id == onlyId)))
        {
            foreach (var issue in document.Validate()) results.Add((document.Family, document.Id, issue));
            foreach (var reference in document.References())
                if (!known.Contains((reference.Family, reference.Id)))
                    results.Add((document.Family, document.Id, new(reference.Path, $"No {reference.Family} '{reference.Id}' in the project.")));
        }
        return results;
    }

    /** Loads every document, optionally for one family. */
    public IReadOnlyList<ContentDocument> LoadAll(string? family = null)
    {
        var documents = new List<ContentDocument>();
        foreach (var (rowFamily, id, _) in List(family))
            try { documents.Add(Load(rowFamily, id)); }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or NotSupportedException or ArgumentException) { }
        return documents;
    }

    /** A new document of a family with the fields a creator fills first. */
    public static ContentDocument NewDocument(string family, string id, string name, GuardianClass guardianClass = GuardianClass.Hunter, string element = "arc")
    {
        ContentDocument document = family switch
        {
            ContentFamilies.Subclass => SubclassTemplates.New(id, name, guardianClass, element),
            ContentFamilies.Ability => new AbilityDefinition { Element = element, Class = guardianClass },
            ContentFamilies.DamageType => new DamageTypeDefinition { BaseElement = DamageTypeDefinition.BuiltIn.Take(4).Contains(element) ? element : "kinetic" },
            ContentFamilies.LootPool => new LootPoolDefinition(),
            ContentFamilies.Enemy => new EnemyDefinition(),
            ContentFamilies.Activity => new ActivityDefinition
            {
                Start = "start",
                Objectives = [new() { Id = "start", Description = "Reach the area", Kind = "travel", Next = ["boss"] },
                    new() { Id = "boss", Description = "Defeat the boss", Kind = "boss", Checkpoint = true }]
            },
            ContentFamilies.Artifact => new ArtifactDefinition
            {
                Columns = [.. Enumerable.Range(0, 5).Select(c => new ArtifactColumn
                {
                    UnlockAfter = c switch { 0 => 0, 1 => 1, 2 => 3, 3 => 5, _ => 7 },
                    Perks = [.. Enumerable.Range(1, 5).Select(p => new ArtifactPerk { Id = $"c{c + 1}-p{p}", Name = $"Column {c + 1} mod {p}" })]
                })]
            },
            _ => throw new ArgumentException($"Unknown content family '{family}'.")
        };
        document.Id = id;
        document.Name = name;
        document.Source ??= "new";
        return document;
    }
}
