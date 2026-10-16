using ImGuiNET;

namespace SunriseStudio.Editor;

/**
 * The Local Server workbench tab (beside Assets and Live): the local WSL sol-server manager (LocalServerPage).
 * View > Local Server switches to it; its label carries a running/stopped/ghost status dot.
 */
public sealed partial class StudioForm
{
    private readonly LocalServerPage localServerPage = new();
    private bool focusLocalServer;

    /** Trailing spaces leave room for the dot; the ### id keeps the tab stable. */
    private const string LocalServerTabLabel = "Local Server     ###local-server-tab";

    private void DrawLocalServerTab()
    {
        bool open = BeginEditorTab(LocalServerTabLabel, focusLocalServer);
        var (color, tip) = localServerPage.Dot();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        float radius = ImGui.GetFontSize() * .22f;
        var center = new System.Numerics.Vector2(max.X - ImGui.GetStyle().FramePadding.X - radius * 2, (min.Y + max.Y) / 2);
        ImGui.GetWindowDrawList().AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(color));
        if (ImGui.IsItemHovered()) StudioUi.Tip(tip);
        if (!open) return;
        focusLocalServer = false;
        shownPage = "local-server";
        localServerPage.DrawBody(workspace.Project?.DirectoryPath, workspace.LastBuild?.OutputDirectory);
        ImGui.EndTabItem();
    }
}
