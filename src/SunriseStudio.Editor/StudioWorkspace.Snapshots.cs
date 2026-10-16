using Solforge.Content.Packaging;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

/** Studio.md Phase 43: every install first copies the installed mod folder, so a bad install rolls back. */
public sealed partial class StudioWorkspace
{
    public const int KeptInstallSnapshots = 5;

    /** Folder where the project keeps install snapshots. */
    public static string SnapshotRoot(StudioProject project) => Path.Combine(project.DirectoryPath, ".solforge", "snapshots");

    /** Runs on the install worker; a failed copy is reported and never blocks the install. */
    private void SnapshotInstalledMod(StudioProject project, string gameRoot)
    {
        if (project.Mod is not { } mod) return;
        try
        {
            var snapshots = new InstallSnapshots(SnapshotRoot(project));
            if (snapshots.Take(ModInstaller.ModsDirectory(gameRoot), mod.Id) is { } taken)
            {
                ReportProgress($"Saved the previous install ({taken.Files:N0} files) for rollback");
                snapshots.Prune(mod.Id, KeptInstallSnapshots);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ReportProgress("Could not snapshot the previous install: " + error.Message);
        }
    }

    /** Restores an install snapshot (newest first from ListInstallSnapshots); the current copy is snapshotted first. */
    public void RestoreInstallSnapshot(string stamp)
    {
        RequireEditable();
        var project = Project!;
        if (project.Mod is not { } mod) throw new InvalidOperationException("Set the mod details first.");
        new InstallSnapshots(SnapshotRoot(project)).Restore(ModInstaller.ModsDirectory(Catalog!.GameRoot), mod.Id, stamp);
    }

    /** Packs the installed mod folder into <folder>/<id>-<version>.solmod (Phase 42); install first. */
    public void PackSolmod(string folder)
    {
        RequireEditable();
        var project = Project!;
        if (project.Mod is not { } mod) throw new InvalidOperationException("Set the mod details first.");
        string installed = Path.Combine(ModInstaller.ModsDirectory(Catalog!.GameRoot), mod.Id);
        if (!Directory.Exists(installed)) throw new InvalidOperationException("Build and install the mod first; the .solmod packs the installed folder.");
        string output = Path.Combine(folder, $"{mod.Id}-{mod.Version}{SolmodContainer.Extension}");
        var manifest = SolmodContainer.Pack(installed, output, mod.Id, mod.Name, mod.Version);
        Notice = new($"Packed {manifest.Files.Count:N0} files into {Path.GetFileName(output)}", false, folder, null);
    }

    /** Restores and says so in the notice bar. */
    public void RestoreInstallSnapshotWithNotice(string stamp)
    {
        RestoreInstallSnapshot(stamp);
        Notice = new($"Restored the install from {stamp}. Restart the game to load it.", false, null, null);
    }

    /** Snapshots of the project's mod, as listed by the snapshot store. */
    public IReadOnlyList<InstallSnapshot> ListInstallSnapshots() =>
        Project is { Mod: { } mod } project ? new InstallSnapshots(SnapshotRoot(project)).List(mod.Id) : [];
}
