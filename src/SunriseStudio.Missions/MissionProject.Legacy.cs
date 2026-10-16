/** File: Reads and migrates the older JSON mission assets of a project into Lua scripts. */
using System.Text;
using System.Text.Json;
using SunriseStudio.Projects;

namespace SunriseStudio.Missions;

/** Node position in a legacy JSON mission. */
public sealed record LegacyMissionPosition(float X, float Y);
/** A legacy JSON mission: format, activity, embedded Lua source and node positions. */
public sealed record LegacyMissionDocument(string Format, int Version, string Activity, string RelativePath,
    string Source, IReadOnlyDictionary<string, LegacyMissionPosition> Positions);
/** A legacy asset read from the project. */
public sealed record LegacyMissionScript(string AssetId, string Label, LegacyMissionDocument Document);

/** Partial: legacy asset handling. */
public static partial class MissionProject
{
    private const string LegacyExtension = ".sunrise-mission.json";

    /** The project's legacy JSON mission assets; migrating one keeps it, so it stays listed. */
    public static IReadOnlyList<AddedProjectAsset> LegacyAssets(StudioProject project) => project.AddedAssets
        .Where(asset => asset.Native is null && asset.Name.EndsWith(LegacyExtension, StringComparison.OrdinalIgnoreCase))
        .OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    /** Reads and checks one legacy JSON asset; a damaged or unsupported one throws. */
    public static LegacyMissionScript ReadLegacy(StudioProject project, string assetId)
    {
        var asset = LegacyAssets(project).SingleOrDefault(value => value.Id == assetId)
            ?? throw new InvalidDataException("The legacy mission source is not in this project.");
        if (asset.Size > 16 * 1024 * 1024) throw new InvalidDataException("Legacy mission source is too large.");
        LegacyMissionDocument data;
        try
        {
            data = JsonSerializer.Deserialize<LegacyMissionDocument>(project.ReadAddedAsset(assetId))
                ?? throw new InvalidDataException("Legacy mission source is empty.");
        }
        catch (JsonException error) { throw new InvalidDataException("Legacy mission source is invalid JSON.", error); }
        if (data.Format != "sunrise-mission-graph" || data.Version != 1 || string.IsNullOrWhiteSpace(data.Activity)
            || data.Source is null || data.Source.Length > 8 * 1024 * 1024 || data.Positions is null
            || data.Positions.Any(pair => pair.Value is null || !float.IsFinite(pair.Value.X) || !float.IsFinite(pair.Value.Y)))
            throw new InvalidDataException("Legacy mission source has an unsupported format.");
        ValidateLegacyPath(data.RelativePath);
        return new(asset.Id, asset.Name[..^LegacyExtension.Length], data);
    }

    /** Migration never overwrites another alternative or removes the original owned archive. */
    public static string MigrateLegacy(StudioProject project, string assetId, string? editedSource = null)
    {
        var legacy = ReadLegacy(project, assetId);
        string source = editedSource ?? legacy.Document.Source;
        string target = PathFor(project, legacy.Document.RelativePath);
        if (File.Exists(target))
        {
            if (File.ReadAllBytes(target).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(source))) return target;
            throw new InvalidOperationException("This activity already has a mod script. Export the legacy alternative or open the existing script.");
        }
        Write(target, source, overwrite: false);
        return target;
    }

    /** Export can recover source text even when a newer graph parser cannot read it. */
    public static string ExportLegacy(StudioProject project, string assetId, string directory, string gameRoot)
    {
        var legacy = ReadLegacy(project, assetId);
        string root = Path.GetFullPath(directory);
        if (Within(root, Path.GetFullPath(gameRoot)) || Within(root, project.DirectoryPath))
            throw new InvalidOperationException("Export legacy source to a review folder outside the installation and project.");
        string relative = legacy.Document.RelativePath.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(root, relative));
        if (!Within(target, root)) throw new InvalidDataException("Legacy mission path leaves its export folder.");
        RejectLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(Encoding.UTF8.GetBytes(legacy.Document.Source)); output.Flush(true);
        return target;
    }

    private static void ValidateLegacyPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || !relative.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
            || relative.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."
                || part.Contains(':') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Legacy mission path is invalid.");
    }
}
