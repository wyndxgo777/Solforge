using System.Text;
using System.Text.Json;
using SunriseStudio.Missions;
using SunriseStudio.Projects;

/** Checks reading legacy mission projects: alternatives are kept, and source, layout, BOM and newlines are not changed by migration. */
internal static class LegacyMissionChecks
{
    /** Entry point: runs every check in this class and throws on the first failure. */
    public static void Run(string root)
    {
        var project = StudioProject.Create(Path.Combine(root, "legacy-project"), "Legacy scripts");
        const string source = "\uFEFF-- owned custom script\r\nreturn { on_start = function(context, state) state.custom = 42 end }\r\n";
        var document = new LegacyMissionDocument("sunrise-mission-graph", 1, "Legacy activity", "legacy/legacy.lua", source,
            new Dictionary<string, LegacyMissionPosition> { ["script/callback"] = new(315, -127) });
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(document);
        var asset = project.AddAsset("First.sunrise-mission.json", original);
        var alternate = project.AddAsset("Second.sunrise-mission.json",
            JsonSerializer.SerializeToUtf8Bytes(document with { Source = source + "-- alternative\r\n" }));
        project = StudioProject.Open(project.DirectoryPath);
        byte[] manifest = File.ReadAllBytes(project.ManifestPath);
        Check(MissionProject.LegacyAssets(project).Count == 2 && MissionProject.List(project).Count == 0,
            "Reading a legacy project migrated or lost an alternative.");
        var restored = MissionProject.ReadLegacy(project, asset.Id);
        Check(restored.Document.Source == source && restored.Document.Positions["script/callback"] == new LegacyMissionPosition(315, -127),
            "Legacy read changed source or layout.");
        string target = MissionProject.MigrateLegacy(project, asset.Id);
        Check(File.ReadAllBytes(target).SequenceEqual(Encoding.UTF8.GetBytes(source)), "Migration changed BOM or newlines.");
        Check(MissionProject.MigrateLegacy(project, asset.Id) == target, "Identical migration was not idempotent.");
        Reject(() => MissionProject.MigrateLegacy(project, alternate.Id));
        Check(File.ReadAllBytes(target).SequenceEqual(Encoding.UTF8.GetBytes(source))
            && File.ReadAllBytes(project.ManifestPath).SequenceEqual(manifest)
            && project.ReadAddedAsset(asset.Id).SequenceEqual(original), "Migration overwrote another script or its source archive.");
        var reopened = StudioProject.Open(project.DirectoryPath);
        Check(MissionProject.LegacyAssets(reopened).Count == 2 && MissionProject.List(reopened).Single().FullPath == target,
            "Reopening lost the migrated script or source alternatives.");

        string game = Path.Combine(root, "legacy-installation"); Directory.CreateDirectory(game);
        var unusual = project.AddAsset("Raw.sunrise-mission.json", JsonSerializer.SerializeToUtf8Bytes(document with
            { RelativePath = "custom/subfolder/raw.lua", Source = "local unknown = '\r\n" }));
        string exported = MissionProject.ExportLegacy(project, unusual.Id, Path.Combine(root, "legacy-review"), game);
        Check(File.ReadAllText(exported) == "local unknown = '\r\n", "Raw recovery required parseable Lua.");
        Reject(() => MissionProject.ExportLegacy(project, unusual.Id, Path.Combine(root, "legacy-review"), game));
        Reject(() => MissionProject.ExportLegacy(project, unusual.Id, game, game));
        Reject(() => MissionProject.ExportLegacy(project, unusual.Id, project.DirectoryPath, game));
        foreach (string path in new[] { "../outside.lua", "/absolute.lua", "x/../../outside.lua", "x/evil:stream.lua" })
        {
            var invalid = project.AddAsset(Guid.NewGuid().ToString("N") + ".sunrise-mission.json",
                JsonSerializer.SerializeToUtf8Bytes(document with { RelativePath = path }));
            Reject(() => MissionProject.ReadLegacy(project, invalid.Id), path);
        }
        var badJson = project.AddAsset("Broken.sunrise-mission.json", Encoding.UTF8.GetBytes("{"));
        Reject(() => MissionProject.ReadLegacy(project, badJson.Id));
        string outside = Path.Combine(root, "legacy-linked-target"); Directory.CreateDirectory(outside);
        string link = Path.Combine(MissionProject.ScriptsRoot(project), "linked"); bool linked = false;
        var linkedAsset = project.AddAsset("Linked.sunrise-mission.json", JsonSerializer.SerializeToUtf8Bytes(document with
            { RelativePath = "linked/linked.lua" }));
        try
        {
            Directory.CreateSymbolicLink(link, outside); linked = true;
            Reject(() => MissionProject.MigrateLegacy(project, linkedAsset.Id));
            Reject(() => MissionProject.List(project));
            Check(!Directory.EnumerateFileSystemEntries(outside).Any(), "Migration wrote through a linked folder.");
        }
        catch (Exception error) when (!linked && error is IOException or UnauthorizedAccessException)
        { Console.WriteLine("mission_legacy linked_directory=unavailable"); }
        finally { if (linked) Directory.Delete(link); }
        Console.WriteLine("mission_legacy discovery=pass source=exact layout=preserved alternatives=retained conflicts=refused recovery=raw");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Reject(Action action, string? input = null)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Unsafe legacy operation was accepted" + (input is null ? "." : ": " + input));
    }
}
