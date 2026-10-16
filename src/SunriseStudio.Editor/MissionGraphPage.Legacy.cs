using System.Numerics;
using ImGuiNET;
using SunriseStudio.Missions;

namespace SunriseStudio.Editor;

/** MissionGraphPage part: lists and opens legacy script alternatives of an older project. */
public sealed partial class MissionGraphPage
{
    private string? legacyAssetId;

    /** Reading an old project never installs or overwrites its script alternatives. */
    private void DrawLegacyScripts(StudioWorkspace workspace)
    {
        if (project is null) return;
        var assets = MissionProject.LegacyAssets(project);
        if (assets.Count == 0) return;
        ImGui.SeparatorText("Legacy scripts");
        foreach (var asset in assets)
        {
            ImGui.PushID(asset.Id);
            string label = asset.Name + " [" + asset.Id[..Math.Min(8, asset.Id.Length)] + "]";
            if (UiAuto.Selectable(label, legacyAssetId == asset.Id)) RequestSwitch(() => OpenLegacy(asset.Id));
            if (ImGui.BeginPopupContextItem("legacy-source"))
            {
                if (UiAuto.MenuItem("Export original Lua"))
                {
                    using var dialog = new FolderBrowserDialog { Description = "Recover legacy Lua source", UseDescriptionForTitle = true };
                    if (dialog.ShowDialog() == DialogResult.OK) Try(() =>
                        Note("Exported " + MissionProject.ExportLegacy(project, asset.Id, dialog.SelectedPath, workspace.Catalog!.GameRoot)));
                }
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
    }

    /** Opens a legacy script as an unsaved mod script, with its saved node positions. */
    private void OpenLegacy(string assetId)
    {
        var installed = catalog ?? throw new InvalidOperationException("Installed scripts did not load.");
        var legacy = MissionProject.ReadLegacy(project!, assetId);
        var document = MissionGraphDocument.Parse(legacy.Document.Source);
        string target = MissionProject.PathFor(project!, legacy.Document.RelativePath);
        var entry = installed.DescribeSource(legacy.Label, legacy.Document.RelativePath, document) with { FullPath = target };
        Adopt(entry, ScriptHome.Project, false, document);
        legacyAssetId = assetId;
        foreach (var (key, value) in legacy.Document.Positions) canvas.Positions[key] = new Vector2(value.X, value.Y);
        IsDirty = true;
        Note("Legacy script. Save makes a mod script; the archive stays.");
    }
}
