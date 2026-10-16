using System.Text.Json.Nodes;
using SunriseStudio.Projects;

/** Checks custom object names in the project manifest: version, trimming and reopen. */
internal static class ObjectNameChecks
{
    /** Names need version 5, survive a reopen, refuse bad text, and their removal returns the old version. */
    public static void Run(string parent)
    {
        var project = StudioProject.Create(Path.Combine(parent, "object-names"), "Object names");
        project.SetObjectName("edit:80FAA28E/StaticInstance/1A0", "  Big tree  ");
        project.SetObjectName("row:marker:spawn:1", "Start");
        var json = JsonNode.Parse(File.ReadAllText(project.ManifestPath))!;
        Check(json["version"]!.GetValue<int>() == 5 && json["objectNames"]!.AsArray().Count == 2,
            "Object names did not save as version 5.");
        var reopened = StudioProject.Open(project.DirectoryPath);
        Check(reopened.ObjectNames.Single(entry => entry.Key.StartsWith("edit:", StringComparison.Ordinal)).Name == "Big tree",
            "A name did not survive a reopen trimmed.");
        Reject(() => reopened.SetObjectName("row:x", new string('a', 201)));
        Reject(() => reopened.SetObjectName("row:x", "two\nlines"));
        Reject(() => reopened.SetObjectName(" ", "Name"));
        reopened.SetObjectName("row:marker:spawn:1", "Renamed");
        Check(reopened.ObjectNames.Count == 2 && reopened.ObjectNames.Any(entry => entry.Name == "Renamed"),
            "Renaming an object added a second entry.");
        reopened.SetObjectName("row:marker:spawn:1", "");
        reopened.SetObjectName("edit:80FAA28E/StaticInstance/1A0", null);
        json = JsonNode.Parse(File.ReadAllText(reopened.ManifestPath))!;
        Check(json["version"]!.GetValue<int>() == 1 && json["objectNames"] is null,
            "Removing every name did not return the project to version 1.");
        var older = JsonNode.Parse(File.ReadAllText(reopened.ManifestPath))!;
        older["objectNames"] = new JsonArray(new JsonObject { ["key"] = "row:a", ["name"] = "A" });
        File.WriteAllText(reopened.ManifestPath, older.ToJsonString());
        Reject(() => StudioProject.Open(reopened.DirectoryPath));
        Console.WriteLine("object_names version=5 reopen=pass trim=pass refused=3 removal=version1 old_version=refused");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("An invalid object name was accepted.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
