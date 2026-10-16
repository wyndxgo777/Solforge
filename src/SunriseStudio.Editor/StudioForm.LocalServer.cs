using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Editor;

/**
 * The Local Server panel (its own area in the panel picker): the local WSL sol-server manager (LocalServerPage).
 * View > Local Server shows and focuses it; its header carries a running/stopped/ghost status dot.
 */
public sealed partial class StudioForm
{
    private readonly LocalServerPage localServerPage = new();
    private bool focusLocalServer;

    private void DrawLocalServerPanel(Vector2 position, Vector2 size)
    {
        if (focusLocalServer) ImGui.SetNextWindowFocus();
        BeginPanel("Local Server", position, size);
        TrackAreaFocus();
        if (focusLocalServer || ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) shownPage = "local-server";
        focusLocalServer = false;
        localServerPage.DrawBody(workspace.Project?.DirectoryPath, workspace.LastBuild?.OutputDirectory);
        ImGui.End();
    }

    /** The status dot at the right end of the panel header's title, left of the picker arrow; hover names the state. */
    private void DrawLocalServerDot(float titleWidth)
    {
        var (color, tip) = localServerPage.Dot();
        var at = ImGui.GetCursorScreenPos();
        float frame = ImGui.GetFrameHeight();
        float radius = ImGui.GetFontSize() * .22f;
        var center = new Vector2(at.X + titleWidth - frame - radius - 4 * scale, at.Y + frame / 2);
        ImGui.GetWindowDrawList().AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(color));
        if (tip.Length != 0 && ImGui.IsWindowHovered()
            && ImGui.IsMouseHoveringRect(center - new Vector2(radius * 2), center + new Vector2(radius * 2)))
            ImGui.SetTooltip(tip);
    }
}
