using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Solforge.Content;

/** One problem in a content document. Path is a JSON-ish path such as "trees[1].nodes[2].entry". */
public sealed record ContentIssue(string Path, string Message, bool Error = true)
{
    public override string ToString() => $"{(Error ? "error" : "warning")}: {Path}: {Message}";
}

/** Collects issues while a document validates. */
public sealed class ContentIssues : List<ContentIssue>
{
    public void Error(string path, string message) => Add(new(path, message));
    public void Warn(string path, string message) => Add(new(path, message, false));
    public bool HasErrors => this.Any(issue => issue.Error);
}

/** A reference from one document to another (family + id), checked by ContentStore. */
public readonly record struct ContentReference(string Family, string Id, string Path);

/**
 * The base of every content family. Unknown members are kept (Unknown) so a newer Solforge's fields
 * survive a round trip through an older one (P20 "unsupported field preservation").
 */
public abstract partial class ContentDocument
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    /** Provenance: "new", "template:<name>", "clone:<id>", "item:0x...", "server:<hash>". */
    public string? Source { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    [JsonIgnore]
    public abstract string Family { get; }

    /** Checks shared by every family, then the family's own. */
    public ContentIssues Validate()
    {
        var issues = new ContentIssues();
        if (Schema < 1 || Schema > CurrentSchema) issues.Error("schema", $"Schema {Schema} is not supported (this Solforge reads 1..{CurrentSchema}).");
        if (!IsValidId(Id)) issues.Error("id", "Id must be 1-64 characters of a-z, 0-9, '_', '-' or '.', starting with a letter or digit.");
        if (string.IsNullOrWhiteSpace(Name)) issues.Error("name", "Name cannot be empty.");
        if (Unknown is { Count: > 0 })
            issues.Warn("", "Kept unknown fields: " + string.Join(", ", Unknown.Keys) + " (written back unchanged).");
        ValidateFamily(issues);
        return issues;
    }

    protected abstract void ValidateFamily(ContentIssues issues);

    public virtual IEnumerable<ContentReference> References() => [];

    public static bool IsValidId(string? id) => id is { Length: >= 1 and <= 64 } && IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]*$")]
    private static partial Regex IdPattern();

    /** "0x1234ABCD" or decimal; null when not a 32-bit hash. */
    public static uint? ParseHash(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out uint hex) ? hex : null;
        return uint.TryParse(text, out uint number) ? number : null;
    }

    public static string Hex(uint value) => "0x" + value.ToString("X8");
}
