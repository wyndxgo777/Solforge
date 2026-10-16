using SunriseStudio.Authoring;
using SunriseStudio.Live;
using SunriseStudio.Missions;
using SunriseStudio.Projects;

namespace SunriseStudio.Editor;

public sealed partial class StudioWorkspace
{
    public ModInstallResult? LastInstall { get; private set; }

    public void SetModMetadata(ModMetadata? metadata)
    {
        RequireEditable();
        Project!.SetMod(metadata);
        ProjectRevision++;
        if (Project.ConfigBindings.Count > 0 && Document is { } document)
        {
            if (document.AddedAssetId is { } id) SelectAdded(id, document.OpenContext);
            else Select(document.Entry.TagHash, true, document.OpenContext);
        }
        Status = "Mod details saved";
        Changed?.Invoke();
    }

    /**
     * Rebuilds saved tables that still carry old copies of grown arrays, so a build ships each once.
     * @return The tables it rewrote.
     */
    public IReadOnlyCollection<uint> CompactSavedTables()
    {
        RequireEditable();
        var edits = SavedTableCompaction.Plan(Project!, Catalog!);
        if (edits.Count == 0) return [];
        MutateProject(() => Project!.ApplyEdits(edits, Catalog!), edits.Keys);
        ProjectRevision++;
        return edits.Keys.ToArray();
    }

    /** Saved mission scripts join the install, so the caller settles an unsaved mission draft first. */
    public void BuildAndInstall()
    {
        RequireEditable();
        CompactSavedTables();
        var project = Project!;
        var catalog = Catalog!;
        BeginWork("Installing mod", () =>
        {
            SnapshotInstalledMod(project, catalog.GameRoot);
            var result = ModInstaller.BuildAndInstall(project, catalog, MissionProject.ModScripts(project), ReportProgress);
            return () =>
            {
                LastInstall = result;
                if (result.Build is not null) LastBuild = result.Build;
                Status = "Mod installed";
                Notice = new("Mod installed. Restart the game to load it.", false, result.ModDirectory,
                    string.Join("\n", (result.Build?.Notes ?? []).Prepend(
                        $"{result.PatchFiles:N0} patches, {result.PackageFiles:N0} packages, {result.ScriptFiles:N0} scripts, "
                        + $"{result.SdkFiles:N0} SDK modules")));
                Changed?.Invoke();
            };
        }, blocking: false);
    }

    /** Replaces changed scripts in the installed mod, then asks the running game to reload it. */
    public void UpdateRunningGame()
    {
        RequireEditable();
        CompactSavedTables();
        var project = Project!;
        var catalog = Catalog!;
        BeginWork("Updating running game", () =>
        {
            var games = LiveConnection.FindProcesses(catalog.GameRoot);
            if (games.Count == 0) throw new InvalidOperationException("The game is not running. Use Build and install.");
            if (games.Count > 1) throw new InvalidOperationException("More than one game is running from this installation.");
            var result = ModInstaller.BuildAndUpdate(project, catalog, MissionProject.ModScripts(project), ReportProgress);
            ReportProgress("Waiting for the game to reload the mod");
            LiveCommandReply reply;
            using (var link = new LiveCommandConnection(games[0].Id))
                reply = link.ReloadModAsync(result.ModId, OperationToken).GetAwaiter().GetResult();
            string text = LiveCommandProtocol.Describe(reply);
            return () =>
            {
                if (result.Build is not null) LastBuild = result.Build;
                Status = text;
                if (reply.Status != LiveCommandStatus.Ok) Error = text;
                Notice = new(text, reply.Status != LiveCommandStatus.Ok, result.ModDirectory,
                    result.Build?.Notes.Count > 0 ? string.Join("\n", result.Build.Notes) : null);
                Changed?.Invoke();
            };
        }, blocking: false);
    }
}
