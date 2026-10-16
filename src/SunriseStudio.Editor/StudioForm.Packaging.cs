using ImGuiNET;

namespace SunriseStudio.Editor;

/** Build menu: .solmod packing (Phase 42) and install snapshots (Phase 43). */
public sealed partial class StudioForm
{
    private void DrawPackagingMenuItems()
    {
        if (ImGui.MenuItem("Pack .solmod...", "", false, workspace.CanEdit && workspace.Project?.Mod is not null))
            PickFolder("Save the .solmod in", folder => workspace.Run(() => workspace.PackSolmod(folder)));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Zips the installed mod with a manifest of every file's size and SHA-256");
        if (!ImGui.BeginMenu("Restore install snapshot", workspace.CanEdit && workspace.Project?.Mod is not null)) return;
        var snapshots = workspace.ListInstallSnapshots();
        if (snapshots.Count == 0) ImGui.TextDisabled("No snapshots yet; each Build and install saves one.");
        foreach (var snapshot in snapshots)
            if (ImGui.MenuItem($"{snapshot.Stamp}  ({snapshot.Files:N0} files, {snapshot.Bytes / 1048576.0:N1} MiB)", "", false, !destiny2Detected))
                workspace.Run(() => workspace.RestoreInstallSnapshotWithNotice(snapshot.Stamp));
        if (destiny2Detected) ImGui.TextDisabled("Close Destiny 2 to restore.");
        ImGui.EndMenu();
    }
}
