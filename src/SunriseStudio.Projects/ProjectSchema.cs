using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SunriseStudio.Projects;

/**
 * The JSON Schema of sunrise.project.json (Solforge Phase 15). The documented copy lives at
 * docs/sunrise.project.schema.json; the ProjectSystem check keeps its property names in step with
 * the manifest records here, so a new manifest field fails verify until the document names it.
 */
public static class ProjectSchema
{
    public const string DocumentedSchemaPath = "docs/sunrise.project.schema.json";

    /** The schema System.Text.Json derives from ProjectManifest with the project serializer options. */
    public static JsonNode Export()
    {
        var options = new JsonSerializerOptions(ProjectJson.Options);
        options.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();
        return options.GetJsonSchemaAsNode(typeof(ProjectManifest),
            new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
    }

    /** Camel-case names of the manifest's top-level members, as the file writes them. */
    public static IReadOnlyList<string> TopLevelProperties() => PropertyNames(typeof(ProjectManifest));

    /** The JSON member names a record writes: camel case unless JsonPropertyName says otherwise; always-ignored members are left out. */
    public static IReadOnlyList<string> PropertyNames(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0
                && property.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always })
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name))
            .ToArray();
    }
}
