using System.Reflection;

namespace SunriseStudio.Packages.TagSchemas;

/**
 * Class hash -> struct schema registry (Solforge Phase 07). Schemas ship in the repo as JSON under
 * src/SunriseStudio.Packages/TagSchemas/schemas and are embedded in this assembly; a folder named by
 * SOLFORGE_TAG_SCHEMAS adds or replaces schemas by id without a rebuild. Hand-written decoders (investment
 * catalog, activity, reward and dialog tables) read their offsets from here, so the files are the one source of truth.
 */
public sealed class TagSchemaRegistry
{
    private const string ResourcePrefix = "SunriseStudio.Packages.TagSchemas.";
    private static readonly Lazy<TagSchemaRegistry> Embedded = new(() => LoadEmbedded(Environment.GetEnvironmentVariable("SOLFORGE_TAG_SCHEMAS")));
    private readonly Dictionary<string, TagSchema> byId;
    private readonly Dictionary<uint, TagSchema> byClass;

    /** The embedded schemas plus any SOLFORGE_TAG_SCHEMAS folder, loaded once. */
    public static TagSchemaRegistry Default => Embedded.Value;

    public IReadOnlyList<TagSchema> All { get; }
    /** Typed-array header marker of the build these schemas describe: 0x80809FBD (86657) unless built for another build. */
    public uint ArrayMarker { get; }
    /** Files or entries that failed to parse or reference unknown schemas; empty when everything loaded. */
    public IReadOnlyList<string> Problems { get; }

    private TagSchemaRegistry(IEnumerable<TagSchema> schemas, List<string> problems, uint arrayMarker)
    {
        ArrayMarker = arrayMarker;
        byId = new(StringComparer.OrdinalIgnoreCase);
        byClass = new();
        foreach (var schema in schemas)
        {
            if (byId.TryGetValue(schema.Id, out var earlier) && earlier.Class is { } old && byClass.GetValueOrDefault(old) == earlier) byClass.Remove(old);
            byId[schema.Id] = schema;
            if (schema.Class is { } type) byClass[type] = schema;
        }
        All = byId.Values.OrderBy(schema => schema.Family, StringComparer.Ordinal).ThenBy(schema => schema.Id, StringComparer.Ordinal).ToList();
        foreach (var schema in All)
            foreach (var field in schema.Fields)
            {
                if (field.Element is { } element && !byId.ContainsKey(element)) problems.Add($"{schema.File}: {schema.Id}.{field.Name} names unknown element schema '{element}'.");
                if (field.Target is { } target && !byId.ContainsKey(target)) problems.Add($"{schema.File}: {schema.Id}.{field.Name} names unknown target schema '{target}'.");
                if (schema.Size > 0 && field.Offset + field.Width > schema.Size)
                    problems.Add($"{schema.File}: {schema.Id}.{field.Name} at 0x{field.Offset:X} runs past the struct size {schema.Size}.");
            }
        Problems = problems;
    }

    /** Builds a registry from (file name, JSON text) pairs; later files replace earlier schemas with the same id. */
    public static TagSchemaRegistry FromSources(IEnumerable<(string File, string Json)> sources) => FromSources(sources, TagSchemaReader.ArrayMarker);

    /**
     * A registry for another build (Solforge.Retail keeps the build 245048 schemas this way): arrayMarker is that build's
     * typed-array header marker, which Validate and Values use. Keep each build's schemas in their own registry.
     */
    public static TagSchemaRegistry FromSources(IEnumerable<(string File, string Json)> sources, uint arrayMarker)
    {
        var problems = new List<string>();
        var schemas = new List<TagSchema>();
        foreach (var (file, json) in sources) schemas.AddRange(TagSchemaJson.Parse(file, json, problems));
        return new TagSchemaRegistry(schemas, problems, arrayMarker);
    }

    /** The embedded schema files, then every *.json in overrideFolder (when it exists). */
    public static TagSchemaRegistry LoadEmbedded(string? overrideFolder = null) => FromSources(EmbeddedSources().Concat(FolderSources(overrideFolder)));

    /** The embedded schema files as (file name, JSON) pairs, in name order. */
    public static IEnumerable<(string File, string Json)> EmbeddedSources()
    {
        var assembly = typeof(TagSchemaRegistry).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
            && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            yield return (name[ResourcePrefix.Length..], Read(assembly, name));
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing schema resource " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IEnumerable<(string File, string Json)> FolderSources(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) yield break;
        foreach (string path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            yield return (path, File.ReadAllText(path));
    }

    /** Schema for a tag class, or null. */
    public TagSchema? Find(uint classHash) => byClass.GetValueOrDefault(classHash);
    /** Schema by id (case-insensitive), or null. */
    public TagSchema? FindId(string id) => byId.GetValueOrDefault(id);
    /** Schema by id, or by class hash given as 0x hex. */
    public TagSchema? Resolve(string idOrClass)
    {
        if (FindId(idOrClass) is { } byName) return byName;
        string text = idOrClass.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint type) ? Find(type) : null;
    }
    /** Schema for a class that must exist (decoders use this). */
    public TagSchema Require(uint classHash) => Find(classHash) ?? throw new InvalidOperationException($"No tag schema for class 0x{classHash:X8}.");
    /** Schema by id that must exist. */
    public TagSchema Require(string id) => FindId(id) ?? throw new InvalidOperationException($"No tag schema '{id}'.");
}
