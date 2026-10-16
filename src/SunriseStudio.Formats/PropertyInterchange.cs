using System.Text.Json;
using System.Text.Json.Serialization;

namespace SunriseStudio.Formats;

/** Exports an asset's editable scalar fields as JSON and applies them back. The JSON document: version, tag, class and the editable values. */
public sealed record PropertyDocument(int Version, uint TagHash, uint ClassHash,
    IReadOnlyList<PropertyValue> Properties);
/** One editable value, located by its name, offset and encoding. */
public sealed record PropertyValue(string Name, int Offset, ScalarEncoding Encoding, string Value);

/** Property JSON carries editable scalars only; it contains no original asset byte payload. */
public static class PropertyInterchange
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /** Writes the inspection's editable fields to a JSON file. */
    public static void Write(string path, AssetInspectionContext context, AssetInspection inspection)
    {
        var properties = Fields(inspection.Sections).Where(x => x.Editable)
            .DistinctBy(x => (x.Name, x.Offset, x.Encoding))
            .Select(x => new PropertyValue(x.Name, x.Offset, x.Encoding, x.Value)).ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(new PropertyDocument(1, context.TagHash,
            context.ClassHash, properties), Options));
    }

    /** Imported fields must still match an editable field in the current asset layout. */
    public static byte[] Apply(string path, AssetInspectionContext context, AssetInspection inspection)
    {
        var document = JsonSerializer.Deserialize<PropertyDocument>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Property document is empty.");
        if (document.Version != 1 || document.TagHash != context.TagHash || document.ClassHash != context.ClassHash
            || document.Properties is null)
            throw new InvalidDataException("Property document belongs to another asset or version.");
        var editable = Fields(inspection.Sections).Where(x => x.Editable)
            .DistinctBy(x => (x.Name, x.Offset, x.Encoding))
            .ToDictionary(x => (x.Name, x.Offset, x.Encoding));
        var result = context.Data.ToArray();
        HashSet<(int, ScalarEncoding)> changed = [];
        foreach (var field in document.Properties)
        {
            if (!editable.TryGetValue((field.Name, field.Offset, field.Encoding), out var property)
                || !changed.Add((field.Offset, field.Encoding)))
                throw new InvalidDataException("Property document contains an unknown or repeated field.");
            if (property.Value != field.Value) result = AssetEdit.Apply(result, property, field.Value);
        }
        return result;
    }

    private static IEnumerable<AssetProperty> Fields(IEnumerable<AssetNode> nodes) =>
        nodes.SelectMany(node => node.Properties.Concat(Fields(node.Children ?? [])));
}
