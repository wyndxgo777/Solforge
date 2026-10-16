using System.Text.Json;
using SunriseStudio.Authoring;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** StudioWorkspace part: queued weapon asset reimport and arms export requests. */
public sealed partial class StudioWorkspace
{
    private uint? pendingWeaponReimport;
    private int weaponReimportRevision;
    private string? weaponReimportManifest;
    private string? weaponReimportPath;
    public PreparedWeaponAssetReimport? LastWeaponAssetReimport { get; private set; }

    /** Queues a reimport of the weapon's art for the form to pick up. */
    public void RequestWeaponAssetReimport(uint weaponTag)
    {
        RequireEditable();
        pendingWeaponReimport = weaponTag;
    }

    private (uint Fire, uint Reload)? pendingArmsExport;

    /** The form asks where to save, then exports the arms rig from these clips. */
    public void RequestWeaponArmsExport(uint fireClip, uint reloadClip) => pendingArmsExport = (fireClip, reloadClip);

    /** Returns and clears the queued arms export (fire and reload clip), if any. */
    internal (uint Fire, uint Reload)? TakeWeaponArmsExportRequest()
    {
        var result = pendingArmsExport;
        pendingArmsExport = null;
        return result;
    }

    /** Returns and clears the queued weapon reimport, if any. */
    internal uint? TakeWeaponAssetReimportRequest()
    {
        uint? result = pendingWeaponReimport;
        pendingWeaponReimport = null;
        return result;
    }

    /** Reads a weapon export's surface report on a worker; done runs between frames. */
    public void InspectWeaponExport(string path, IReadOnlyList<WeaponArtVariant> variants,
        Action<OriginalAssetInspection?, string> done)
    {
        var catalog = Catalog ?? throw new InvalidOperationException("Open your Sunrise installation first.");
        InspectExport(() => WeaponOriginalAssetImporter.Inspect(catalog, path, variants), done);
    }

    /** Reads an original asset's surface report on a worker; done runs between frames. */
    public void InspectOriginalAsset(string path, Action<OriginalAssetInspection?, string> done) =>
        InspectExport(() => OriginalAssetImporter.Inspect(path), done);

    private void InspectExport(Func<OriginalAssetInspection> inspect, Action<OriginalAssetInspection?, string> done)
    {
        string status = Status;
        BeginWork("Reading Blender export", () =>
        {
            OriginalAssetInspection? result = null;
            string error = "";
            try { result = inspect(); }
            catch (Exception failure) when (failure is not OperationCanceledException) { error = failure.Message; }
            return () => { done(result, error); Status = status; };
        }, blocking: false, keepNotice: true);
    }

    /** The export path the weapon's receipt names, or empty when that file is gone. */
    public string WeaponExportPath(uint weaponTag)
    {
        if (Project is not { } project || Catalog is not { } catalog) return "";
        foreach (var asset in project.AddedAssets.Where(asset => asset.Native is null && asset.Size <= 1024 * 1024
            && asset.Name.EndsWith(".creation.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var json = JsonDocument.Parse(project.ReadAddedAsset(asset.Id, catalog));
                var root = json.RootElement;
                if (!root.TryGetProperty("weaponTag", out var tag) || tag.GetUInt32() != weaponTag) continue;
                if (!root.TryGetProperty("assetImport", out var import) || !import.TryGetProperty("sourcePath", out var stored)
                    || stored.GetString() is not { Length: > 0 } value) return "";
                // A relative path is relative to the project folder.
                string path = Path.GetFullPath(value, project.DirectoryPath);
                return File.Exists(path) ? path : "";
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException or FormatException or ArgumentException) { }
        }
        return "";
    }

    /** Preflight runs in the background and publishes only a reviewable import plan. */
    public void PrepareWeaponAssetReimport(uint weaponTag, string path)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        string manifest = ManifestStamp(project);
        int revision = ProjectRevision;
        long generation = catalog.ReadGeneration, overlay = catalog.OverlayRevision;
        LastWeaponAssetReimport = null; weaponReimportManifest = null; Error = "";
        BeginWork("Checking Blender weapon", () =>
        {
            string installation = catalog.GetPackageFileIdentity(OperationToken);
            var prepared = WeaponAssetReimporter.Prepare(project, catalog, weaponTag, path, OperationToken);
            if (catalog.GetPackageFileIdentity(OperationToken) != installation)
                throw new InvalidDataException("The installation changed. Check the Blender export again.");
            return () =>
            {
                OperationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(Project, project) || !ReferenceEquals(Catalog, catalog)
                    || ProjectRevision != revision || catalog.ReadGeneration != generation || catalog.OverlayRevision != overlay
                    || ManifestStamp(project) != manifest)
                    throw new InvalidDataException("The project changed. Check the Blender export again.");
                LastWeaponAssetReimport = prepared;
                weaponReimportRevision = revision; weaponReimportManifest = manifest; weaponReimportPath = path;
                Status = $"Blender reimport ready: {prepared.Changes.Count} assets change";
            };
        }, blocking: false, keepNotice: true);
    }

    /** The full hash check runs on a worker; resources and the receipt share one undo step. */
    public void ApplyWeaponAssetReimport(PreparedWeaponAssetReimport prepared)
    {
        RequireEditable();
        var project = Project!; var catalog = Catalog!;
        int revision = weaponReimportRevision;
        string? path = weaponReimportPath;
        if (!ReferenceEquals(LastWeaponAssetReimport, prepared) || ProjectRevision != revision
            || weaponReimportManifest is null || ManifestStamp(project) != weaponReimportManifest)
            throw new InvalidDataException("The import preview is no longer current. Check the Blender export again.");
        BeginWork("Reimporting Blender weapon", () =>
        {
            WeaponAssetReimporter.Validate(project, catalog, prepared);
            return () =>
            {
                if (!ReferenceEquals(Project, project) || ProjectRevision != revision)
                    throw new InvalidDataException("The import preview is no longer current. Check the Blender export again.");
                ApplyAssetReplacement(prepared.Additions, new ProjectAssetEdits(prepared.Changes, prepared.AddedChanges),
                    prepared.Removals, "Blender weapon reimported");
                LastWeaponAssetReimport = null; weaponReimportManifest = null;
                // Per-shot bursts sit outside the import layout and follow the export as a second step.
                QueueFiringEffects(prepared.Profile.WeaponTag, path);
            };
        });
    }

    /** Changes whenever the manifest file is rewritten. */
    private static string ManifestStamp(StudioProject project)
    {
        var info = new FileInfo(project.ManifestPath);
        return info.Exists ? $"{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "";
    }
}
