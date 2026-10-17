using System.Text.Json.Nodes;
using ImGuiNET;
using SunriseStudio.Viewport;

namespace SunriseStudio.Editor;

/**
 * Help > Keybinds & Tips (F1): every keybind from the live shortcut map, searchable and grouped by
 * area, plus the tips page. get_help (MCP) answers from the same HelpCatalog.
 */
public sealed partial class StudioForm
{
    private const string HelpTitle = "Keybinds & Tips";
    private bool helpDialog;
    private string helpQuery = "";
    private int helpArea;
    private long helpShortcutFrame = -1;

    private void HandleHelpShortcut()
    {
        if (!ShortcutInput.Pressed(ShortcutActions.OpenHelp) || helpShortcutFrame == ShortcutInput.Frame) return;
        helpShortcutFrame = ShortcutInput.Frame;
        helpDialog = true;
    }

    private void DrawHelpMenuItems()
    {
        if (UiAuto.MenuItem("Keybinds & Tips...", ShortcutInput.Text(ShortcutActions.OpenHelp))) helpDialog = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Every shortcut with your current keys, and workflow, performance and MCP tips");
    }

    private void DrawHelpDialog()
    {
        HandleHelpShortcut();
        if (helpDialog) { ImGui.OpenPopup(HelpTitle); helpDialog = false; }
        ImGui.SetNextWindowPos(new(ClientSize.Width / 2f, ClientSize.Height / 2f), ImGuiCond.Appearing, new(.5f));
        ImGui.SetNextWindowSize(new(Math.Min(900 * scale, ClientSize.Width - 40), Math.Min(640 * scale, ClientSize.Height - 40)), ImGuiCond.Appearing);
        bool open = true;
        if (!ImGui.BeginPopupModal(HelpTitle, ref open, ImGuiWindowFlags.NoSavedSettings)) return;
        ImGui.SetNextItemWidth(320 * scale);
        UiAuto.InputTextWithHint("##help-find", "Search keybinds and tips", ref helpQuery, 128);
        ImGui.SameLine();
        string[] areas = ["All areas", .. HelpCatalog.Areas];
        ImGui.SetNextItemWidth(200 * scale);
        UiAuto.Combo("##help-area", ref helpArea, areas, areas.Length);
        string? area = helpArea > 0 && helpArea < areas.Length ? areas[helpArea] : null;
        float footer = ImGui.GetFrameHeightWithSpacing();
        if (ImGui.BeginTabBar("help-tabs"))
        {
            if (UiAuto.BeginTabItem("Keybinds"))
            {
                DrawHelpKeys(HelpCatalog.Search(ShortcutInput.Map, helpQuery, area), footer);
                UiAuto.EndTabItem();
            }
            if (UiAuto.BeginTabItem("Tips"))
            {
                if (ImGui.BeginChild("help-tips", new(0, -footer), ImGuiChildFlags.Borders))
                {
                    string? topic = null;
                    foreach (var tip in HelpCatalog.SearchTips(helpQuery))
                    {
                        if (tip.Topic != topic) { topic = tip.Topic; ImGui.Spacing(); ImGui.TextColored(StudioTheme.Accent, topic); ImGui.Separator(); }
                        ImGui.TextUnformatted(tip.Title);
                        ImGui.Indent(); ImGui.PushTextWrapPos(0); ImGui.TextDisabled(tip.Text); ImGui.PopTextWrapPos(); ImGui.Unindent();
                    }
                }
                ImGui.EndChild();
                UiAuto.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        if (UiAuto.Button("Edit shortcuts...")) { ImGui.CloseCurrentPopup(); shortcutsDialog = true; }
        ImGui.SameLine();
        if (UiAuto.Button("Close", new(100 * scale, 0))) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawHelpKeys(IReadOnlyList<HelpKey> keys, float footer)
    {
        if (!ImGui.BeginTable("help-keys", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, new(0, -footer))) return;
        ImGui.TableSetupColumn("Area", ImGuiTableColumnFlags.WidthFixed, 170 * scale);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Keys", ImGuiTableColumnFlags.WidthFixed, 280 * scale);
        ImGui.TableSetupScrollFreeze(0, 1); ImGui.TableHeadersRow();
        string? last = null;
        foreach (var key in keys)
        {
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            if (key.Area != last) { ImGui.TextColored(StudioTheme.Accent, key.Area); last = key.Area; }
            ImGui.TableNextColumn(); ImGui.TextUnformatted(key.Action);
            if (!key.Rebindable && ImGui.IsItemHovered()) ImGui.SetTooltip("Built in; not rebindable");
            ImGui.TableNextColumn();
            if (key.Keys == "Unbound") ImGui.TextDisabled(key.Keys); else ImGui.TextUnformatted(key.Keys);
        }
        ImGui.EndTable();
    }

    /** get_help: keybinds (current keys) and tips, filtered by query, area and section. */
    private static JsonObject AiHelp(JsonObject args)
    {
        string? query = Solforge.Mcp.McpJson.String(args, "query"), area = Solforge.Mcp.McpJson.String(args, "area");
        string section = Solforge.Mcp.McpJson.String(args, "section") ?? "all";
        var result = new JsonObject { ["areas"] = new JsonArray(HelpCatalog.Areas.Select(name => (JsonNode)name).ToArray()) };
        if (section is "all" or "keybinds")
            result["keybinds"] = new JsonArray(HelpCatalog.Search(ShortcutInput.Map, query, area).Select(key => (JsonNode)new JsonObject
            {
                ["area"] = key.Area, ["action"] = key.Action, ["keys"] = key.Keys, ["id"] = key.Id, ["rebindable"] = key.Rebindable
            }).ToArray());
        if (section is "all" or "tips")
            result["tips"] = new JsonArray(HelpCatalog.SearchTips(query).Select(tip => (JsonNode)new JsonObject
            {
                ["topic"] = tip.Topic, ["title"] = tip.Title, ["text"] = tip.Text
            }).ToArray());
        if (section is "all" or "tools")
        {
            // Which MCP tool does each menu, panel and dialog (Solforge.Mcp ToolMap and EditorCommands).
            result["tools"] = Solforge.Mcp.ToolMap.ToJson(query);
            if (section == "tools") result["commands"] = Solforge.Mcp.EditorCommands.ToJson(query, null);
            result["tools_hint"] = Solforge.Mcp.SolforgeTools.CommandsHint;
        }
        result["preset"] = ShortcutInput.Map.Title;
        return result;
    }
}
