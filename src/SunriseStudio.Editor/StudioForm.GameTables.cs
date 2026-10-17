using System.Text.Json.Nodes;
using ImGuiNET;

namespace SunriseStudio.Editor;

/**
 * The Data explorer's "Game tables" tab: read-only pages for the tables the tag schema registry decodes (activities,
 * client reward lists, dialog tables) and the registry itself (schemas, fields with confirmed/guessed status, validation).
 * Every page calls the MCP tool an AI client gets (list_activities, get_activity, get_loot_table, get_dialog_table,
 * list_tag_schemas, get_tag_schema, validate_tag), so both always see the same decode. Nothing here edits a tag.
 */
public sealed partial class StudioForm
{
    private string gameActivityFilter = "", gameDialogFilter = "", gameSchemaFilter = "", gameSelectedActivity = "", gameSelectedDialog = "", gameSelectedSchema = "";
    private int gameLootSource, gameLootIndex = -1;
    private volatile JsonArray gameActivityRows = [], gameLootRows = [], gameDialogRows = [], gameSchemaRows = [];
    private volatile string gameActivityDetail = "", gameLootDetail = "", gameDialogDetail = "", gameSchemaDetail = "";

    private void DrawDataGameTables()
    {
        if (!ImGui.BeginTabBar("game-tables")) return;
        if (ImGui.BeginTabItem("Activities")) { DrawGameActivities(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Rewards")) { DrawGameLoot(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Dialog")) { DrawGameDialog(); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Schemas")) { DrawGameSchemas(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    private void DrawGameActivities()
    {
        ImGui.SetNextItemWidth(260 * scale);
        bool go = ImGui.InputText("Filter##activities", ref gameActivityFilter, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("List activities") || go)
            DataRun("list_activities", new JsonObject { ["filter"] = gameActivityFilter, ["limit"] = 500 }, (text, error) => gameActivityRows = Rows(text, error));
        ImGui.SameLine();
        if (ImGui.Button("Export CSV##activities")) DataExport("list_activities", new JsonObject { ["filter"] = gameActivityFilter });
        ImGui.TextDisabled("Read-only. Fields marked guessed in the detail are data-only; get_tag_schema activity_definition gives each field's source.");
        GameList("activities", gameActivityRows, row => $"{row["internal_name"]}  {row["name"]}  L{row["required_level"]} P{row["required_power"]}",
            row => row["hash"]?.ToString() ?? "", ref gameSelectedActivity,
            hash => DataRun("get_activity", new JsonObject { ["activity"] = hash }, (text, _) => gameActivityDetail = text), gameActivityDetail);
    }

    private void DrawGameLoot()
    {
        ImGui.SetNextItemWidth(200 * scale);
        ImGui.Combo("Source##loot", ref gameLootSource, "Activity reward sets\0Progression rewards\0");
        ImGui.SameLine();
        string source = gameLootSource == 1 ? "progression" : "activity";
        if (ImGui.Button("List##loot"))
            DataRun("get_loot_table", new JsonObject { ["source"] = source, ["limit"] = 500 }, (text, error) => gameLootRows = Rows(text, error));
        ImGui.TextDisabled("The client's reward lists. Drop rolls (real loot tables) live on the server and are not in the packages.");
        string selected = gameLootIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        GameList("loot", gameLootRows, row => $"{row["kind"]} {row["index"]}  {row["hash"]}  groups {row["groups"]}  items {row["items"]}",
            row => row["index"]?.ToString() ?? "", ref selected,
            index =>
            {
                if (!int.TryParse(index, out int value)) return;
                gameLootIndex = value;
                DataRun("get_loot_table", new JsonObject { ["source"] = source, ["index"] = value }, (text, _) => gameLootDetail = text);
            }, gameLootDetail);
    }

    private void DrawGameDialog()
    {
        ImGui.SetNextItemWidth(260 * scale);
        bool go = ImGui.InputText("Filter##dialog", ref gameDialogFilter, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("List dialog tables") || go)
            DataRun("get_dialog_table", new JsonObject { ["filter"] = gameDialogFilter, ["limit"] = 500 }, (text, error) => gameDialogRows = Rows(text, error));
        ImGui.TextDisabled("Variant delay, weight, audio and duration are guessed from data; text comes from the client text reader layout.");
        GameList("dialog", gameDialogRows, row => $"{row["hash"]}  {row["name"]}  {row["package"]}  {row["size"]} bytes",
            row => row["hash"]?.ToString() ?? "", ref gameSelectedDialog,
            hash => DataRun("get_dialog_table", new JsonObject { ["hash"] = hash }, (text, _) => gameDialogDetail = text), gameDialogDetail);
    }

    private void DrawGameSchemas()
    {
        ImGui.SetNextItemWidth(260 * scale);
        bool go = ImGui.InputText("Filter##schemas", ref gameSchemaFilter, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if (ImGui.Button("List schemas") || go || gameSchemaRows.Count == 0 && ImGui.IsWindowAppearing())
            DataRun("list_tag_schemas", new JsonObject { ["filter"] = gameSchemaFilter, ["limit"] = 500 }, (text, error) => gameSchemaRows = Rows(text, error));
        ImGui.SameLine();
        ImGui.BeginDisabled(gameSelectedSchema.Length == 0 || !gameSelectedSchema.StartsWith("0x", StringComparison.Ordinal));
        if (ImGui.Button("Validate 50 tags of this class"))
            DataRun("validate_tag", new JsonObject { ["class"] = gameSelectedSchema, ["samples"] = 50 }, (text, _) => gameSchemaDetail = text);
        ImGui.EndDisabled();
        ImGui.TextDisabled("Schema files: src/SunriseStudio.Packages/TagSchemas/schemas (SOLFORGE_TAG_SCHEMAS adds a folder).");
        GameList("schemas", gameSchemaRows, row => $"[{row["family"]}] {row["id"]}  {row["name"]}  fields {row["fields"]} (guessed {row["guessed"]})",
            row => row["class"]?.ToString() ?? row["id"]?.ToString() ?? "", ref gameSelectedSchema,
            id => DataRun("get_tag_schema", new JsonObject { ["schema"] = id }, (text, _) => gameSchemaDetail = text), gameSchemaDetail);
    }

    /** A selectable row list on the left and the selected row's tool output (read-only text) on the right. */
    private void GameList(string id, JsonArray rows, Func<JsonObject, string> label, Func<JsonObject, string> key, ref string selected, Action<string> open, string detail)
    {
        ImGui.BeginChild(id + "-list", new(420 * scale, 0), ImGuiChildFlags.Borders);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] is not JsonObject row) continue;
            string value = key(row), text = label(row);
            if (ImGui.Selectable($"{(text.Length > 140 ? text[..140] : text)}##{id}{i}", value == selected) && value.Length > 0)
            { selected = value; open(value); }
        }
        if (rows.Count == 0) ImGui.TextDisabled("Nothing listed yet.");
        ImGui.EndChild();
        ImGui.SameLine();
        string output = detail;
        ImGui.InputTextMultiline("##" + id + "-detail", ref output, (uint)Math.Max(1024, output.Length + 1), ImGui.GetContentRegionAvail(), ImGuiInputTextFlags.ReadOnly);
    }
}
