using System.Numerics;
using ImGuiNET;

namespace SunriseStudio.Editor;

/**
 * The unified Graph panel's shared views: every graph model (mission script, node graph) shows the same Nodes,
 * source (Lua or the model's own source) and Checks tabs, and lists problems the same way.
 */
internal static class GraphViews
{
    public static readonly Vector4 ProblemColor = new(1, .55f, .45f, 1);

    /** The three tabs, plus an optional extra one (the mission script's Retail tab). The ### id keeps Checks selected while its count changes. */
    public static void Tabs(string id, Action nodes, string sourceLabel, Action source, int problems, Action checks,
        (string Label, Action Draw)? extra = null)
    {
        if (!ImGui.BeginTabBar(id)) return;
        if (UiAuto.BeginTabItem("Nodes")) { nodes(); UiAuto.EndTabItem(); }
        if (UiAuto.BeginTabItem(sourceLabel)) { source(); UiAuto.EndTabItem(); }
        if (UiAuto.BeginTabItem(problems > 0 ? $"Checks ({problems})###checks" : "Checks###checks")) { checks(); UiAuto.EndTabItem(); }
        if (extra is { } tab && UiAuto.BeginTabItem(tab.Label)) { tab.Draw(); UiAuto.EndTabItem(); }
        ImGui.EndTabBar();
    }

    /** One wrapped line in the problem color. */
    public static void Problem(string text, Vector4? color = null)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color ?? ProblemColor);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
