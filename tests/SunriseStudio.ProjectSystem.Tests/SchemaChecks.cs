using System.Text.Json.Nodes;
using SunriseStudio.Projects;
using static SunriseStudio.ProjectSystem.Tests.Check;

namespace SunriseStudio.ProjectSystem.Tests;

/** Phase 15: docs/sunrise.project.schema.json names exactly the members the manifest records write. */
internal static class SchemaChecks
{
    internal static void Run(List<string> checks)
    {
        using var stream = typeof(SchemaChecks).Assembly.GetManifestResourceStream("Solforge.ProjectSchema")
            ?? throw new InvalidOperationException("The documented schema is not embedded.");
        var document = JsonNode.Parse(stream)!.AsObject();
        Same(document["properties"]!.AsObject(), ProjectSchema.TopLevelProperties(), "sunrise.project.json");
        var definitions = document["$defs"]!.AsObject();
        Same(definitions["change"]!["properties"]!.AsObject(), ProjectSchema.PropertyNames(typeof(ProjectAssetChange)), "changes[]");
        Same(definitions["addedAsset"]!["properties"]!.AsObject(), ProjectSchema.PropertyNames(typeof(AddedProjectAsset)), "addedAssets[]");
        Same(definitions["native"]!["properties"]!.AsObject(), ProjectSchema.PropertyNames(typeof(NativeAssetMetadata)), "native");
        Same(definitions["mod"]!["properties"]!.AsObject(), ProjectSchema.PropertyNames(typeof(ModMetadata)), "mod");
        Same(definitions["objectName"]!["properties"]!.AsObject(), ProjectSchema.PropertyNames(typeof(ProjectObjectName)), "objectNames[]");
        checks.Add("documented project schema names every manifest member");

        var exported = ProjectSchema.Export();
        var exportedNames = exported["properties"]?.AsObject().Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal) ?? [];
        Require(ProjectSchema.TopLevelProperties().All(exportedNames.Contains), "The exported schema misses a manifest member.");
        checks.Add("derived JSON schema agrees with the documented one");
    }

    private static void Same(JsonObject documented, IReadOnlyList<string> actual, string where)
    {
        var names = documented.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var missing = actual.Where(name => !names.Contains(name)).ToArray();
        var extra = names.Where(name => !actual.Contains(name, StringComparer.Ordinal)).ToArray();
        Require(missing.Length == 0, $"{where}: the schema document does not name {string.Join(", ", missing)}.");
        Require(extra.Length == 0, $"{where}: the schema document names unknown {string.Join(", ", extra)}.");
    }
}
