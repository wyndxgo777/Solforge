using System.Text.Json.Nodes;
using ImGuiNET;
using Solforge.Mcp;

namespace SunriseStudio.Editor;

/**
 * T1.4 "Where is this used" panel: the get_item_sources report for one item (drops, vendors, collections, plugs,
 * activities, records, quests and the project's edits), built off the UI thread. Opened from the item page and the
 * asset browser's context menu.
 */
public sealed partial class StudioForm
{
    private JsonObject? itemSources;
    private string itemSourcesNote = "";
    private string itemSourcesFilter = "";
    private bool openItemSources;
    private CancellationTokenSource? itemSourcesCancel;

    /** Starts the report for an item tag and opens the panel. */
    private void BeginItemSources(uint tag)
    {
        itemSourcesCancel?.Cancel();
        itemSources = null;
        openItemSources = true;
        var (capture, note) = CaptureItemSources();
        if (capture is null) { itemSourcesNote = note ?? "The item catalog is not loaded."; return; }
        itemSourcesNote = "Searching loot pools, vendors, collections, plugs and references...";
        var cancel = itemSourcesCancel = new CancellationTokenSource();
        string item = Hex(tag);
        _ = Task.Run(() =>
        {
            JsonObject report;
            try { report = ItemSourcesReport(capture, item, null, true, cancel.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or IOException or KeyNotFoundException)
            { report = AiError(error.Message); }
            if (cancel.IsCancellationRequested) return;
            Volatile.Write(ref itemSources, report);
        });
    }

    private void DrawItemSources()
    {
        if (InvestmentPage.TakeWhereUsedRequest() is { } requested) BeginItemSources(requested);
        if (openItemSources) { ImGui.OpenPopup("Where is this used"); openItemSources = false; }
        if (!ImGui.BeginPopupModal("Where is this used", ImGuiWindowFlags.AlwaysAutoResize)) return;
        var report = Volatile.Read(ref itemSources);
        if (report is null) ImGui.TextWrapped(itemSourcesNote);
        else if (report["error"] is JsonNode error) ImGui.TextWrapped(error.ToString());
        else
        {
            var item = report["item"];
            ImGui.TextUnformatted($"{item?["name"]}  [{item?["tag"]}]  {item?["rarity"]} {item?["type"]}");
            ImGui.TextDisabled(report["counts"] is JsonObject { Count: > 0 } counts
                ? string.Join("   ", counts.Select(pair => $"{pair.Key} {pair.Value}")) : "No sources or uses found.");
            foreach (var line in (report["notes"] as JsonArray ?? new JsonArray()).Concat(report["truncated"] as JsonArray ?? new JsonArray()))
                ImGui.TextDisabled(line?.ToString() ?? "");
            UiAuto.InputTextWithHint("##item-sources-filter", "filter", ref itemSourcesFilter, 128);
            if (ImGui.BeginChild("Item sources rows", new(760 * scale, 360 * scale), ImGuiChildFlags.Borders)
                && ImGui.BeginTable("item-sources", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
            {
                ImGui.TableSetupColumn("Kind");
                ImGui.TableSetupColumn("Source");
                ImGui.TableSetupColumn("Detail");
                ImGui.TableSetupColumn("");
                ImGui.TableHeadersRow();
                int index = 0;
                foreach (var row in report["rows"] as JsonArray ?? new JsonArray())
                {
                    index++;
                    if (row is null || (itemSourcesFilter.Length > 0 && !row.ToJsonString().Contains(itemSourcesFilter, StringComparison.OrdinalIgnoreCase))) continue;
                    ImGui.PushID(index);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row["kind"]?.ToString() ?? "");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(row["source"]?.ToString() ?? "");
                    if (row["edited"] is not null) { ImGui.SameLine(); ImGui.TextDisabled("(edited)"); }
                    ImGui.TableNextColumn(); ImGui.TextWrapped(row["detail"]?.ToString() ?? "");
                    ImGui.TableNextColumn();
                    if (SolforgeTools.ParseHash(row["tag"]?.ToString()) is { } tag && UiAuto.SmallButton("Open"))
                    {
                        workspace.Open(tag);
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            ImGui.EndChild();
        }
        if (UiAuto.Button("Close"))
        {
            itemSourcesCancel?.Cancel();
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }
}
